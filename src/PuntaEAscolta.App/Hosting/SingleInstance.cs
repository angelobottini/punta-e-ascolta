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

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _event;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    private SingleInstance(Mutex mutex, bool owned, EventWaitHandle? evt)
    {
        _mutex = mutex;
        IsFirst = owned;
        _event = evt;
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

        EventWaitHandle? evt = null;
        if (owned)
        {
            evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        return new SingleInstance(mutex, owned, evt);
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
                using (evt) return evt.Set();
            }
        }
        catch (Exception)
        {
            // Nessuna istanza raggiungibile: la seconda istanza esce comunque.
        }
        return false;
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
        _event?.Dispose();
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* thread diverso: il sistema lo rilascia all'uscita */ }
        }
        _mutex.Dispose();
    }
}
