namespace PuntaEAscolta.App.Hosting;

/// <summary>
/// Istanza singola per sessione utente: mutex locale più un evento con nome con cui una seconda istanza chiede alla prima
/// di aprire la finestra impostazioni (doppio clic sull'eseguibile con l'app già attiva).
/// Il nome non dipende dalla cartella: due copie (x64 e ARM64) non devono intercettare il mouse insieme.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PuntaEAscolta.IstanzaUnica";
    private const string EventName = @"Local\PuntaEAscolta.ApriImpostazioni";
    private const string ExitEventName = @"Local\PuntaEAscolta.Esci";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _event;
    private readonly EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _wait;
    private RegisteredWaitHandle? _exitWait;
    private bool _disposed;

    private SingleInstance(Mutex mutex, bool owned, EventWaitHandle? evt, EventWaitHandle? exitEvent)
    {
        _mutex = mutex;
        IsFirst = owned;
        _event = evt;
        _exitEvent = exitEvent;
    }

    public bool IsFirst { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // L'istanza precedente è terminata senza rilasciare il mutex: ora è nostro.
            owned = true;
        }

        EventWaitHandle? evt = null, exitEvent = null;
        if (owned)
        {
            evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        }
        return new SingleInstance(mutex, owned, evt, exitEvent);
    }

    /// <summary>Vero se l'app con l'icona di notifica è in esecuzione in questa sessione.</summary>
    public static bool IsRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var existing))
            {
                existing.Dispose();
                return true;
            }
        }
        catch (Exception)
        {
            // Accesso negato o nome non valido: si considera non in esecuzione.
        }
        return false;
    }

    /// <summary>Chiamato dalla seconda istanza: chiede alla prima di aprire le impostazioni.</summary>
    public static bool SignalFirstInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var evt))
            {
                // Questa istanza è stata lanciata dall'utente ed è in primo piano: cede il permesso alla prima.
                Native.NativeMethods.AllowSetForegroundWindow(Native.NativeMethods.AsfwAny);
                using (evt) return evt.Set();
            }
        }
        catch (Exception)
        {
            // Nessuna istanza raggiungibile: la seconda istanza esce comunque.
        }
        return false;
    }

    /// <summary>Chiede all'istanza in esecuzione di chiudersi (comando --exit).</summary>
    public static bool SignalExit()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var evt))
            {
                using (evt) return evt.Set();
            }
        }
        catch (Exception)
        {
            // Nessuna istanza raggiungibile.
        }
        return false;
    }

    /// <summary>Esegue <paramref name="onExit"/> (su un thread del pool) quando il comando --exit lo chiede.</summary>
    public void ListenForExit(Action onExit)
    {
        ArgumentNullException.ThrowIfNull(onExit);
        if (_exitEvent is null || _exitWait is not null) return;
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) =>
        {
            try { onExit(); } catch { /* l'azione registra da sé i propri errori */ }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>Esegue <paramref name="onActivate"/> (su un thread del pool) ogni volta che una seconda istanza bussa.</summary>
    public void Listen(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        if (_event is null || _wait is not null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) =>
        {
            try { onActivate(); } catch { /* l'azione registra da sé i propri errori */ }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _wait?.Unregister(null); } catch { /* ignorato */ }
        try { _exitWait?.Unregister(null); } catch { /* ignorato */ }
        _event?.Dispose();
        _exitEvent?.Dispose();
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* thread diverso: il sistema lo rilascia all'uscita */ }
        }
        _mutex.Dispose();
    }
}
