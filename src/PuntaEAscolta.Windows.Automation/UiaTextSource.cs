using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Automation;

/// <summary>
/// Accesso al testo dell'interfaccia tramite UI Automation COM (UIA3, CUIAutomation8).
/// Tutte le chiamate UIA girano su un thread MTA dedicato con coda di lavoro; un cane da guardia di 1400 ms
/// restituisce null, abbandona il thread bloccato e ne crea uno nuovo. Mai focus, mai finestre, mai eccezioni al chiamante.
/// </summary>
public sealed class UiaTextSource : IUiTextSource
{
    /// <summary>
    /// Tempo massimo concesso a una richiesta prima di rispondere null e sostituire il thread. Deve restare sopra il tempo
    /// della transazione UIA più un margine (<see cref="UiaSession.TransactionTimeoutMs"/> + 200) e sotto il tempo massimo
    /// di una fase del risolutore (TextResolver.DefaultUiaTimeoutMs, 1500 ms): vedi UiaTimeoutBudgetTests.
    /// </summary>
    public const int WatchdogMs = 1400;

    /// <summary>Margine minimo fra la scadenza della transazione UIA e il cane da guardia.</summary>
    internal const int WatchdogMarginMs = 200;

    private readonly ILog _log;
    private readonly ProcessNameCache _processNames = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _workerLock = new();
    private UiaWorker _worker;
    private int _generation;
    private volatile bool _disposed;

    public UiaTextSource(ILog log)
    {
        _log = log ?? NullLog.Instance;
        _worker = new UiaWorker(_log, ++_generation);
    }

    public Task<UiElementInfo?> GetElementAtAsync(ScreenPoint point, CancellationToken ct) =>
        RunAsync(session => new ElementReader(session, _log, _processNames).GetElementAt(point), "GetElementAt", ct);

    public Task<UiSelectionInfo?> GetSelectionAsync(CancellationToken ct) =>
        RunAsync(session => new ElementReader(session, _log, _processNames).GetSelection(), "GetSelection", ct);

    public Task<UiTooltipInfo?> FindTooltipAsync(ScreenPoint point, CancellationToken ct) =>
        RunAsync(session => new ElementReader(session, _log, _processNames).FindTooltip(point), "FindTooltip", ct);

    /// <summary>Serializza le richieste, le esegue sul thread UIA e applica il cane da guardia. Non lancia mai.</summary>
    private async Task<T?> RunAsync<T>(Func<UiaSession, T?> work, string operation, CancellationToken ct) where T : class
    {
        if (_disposed) return null;
        try
        {
            // una sola richiesta in volo: così il cane da guardia misura il lavoro vero e non l'attesa in coda
            if (!await _gate.WaitAsync(WatchdogMs, ct).ConfigureAwait(false))
            {
                _log.Warn($"UIA: {operation} rinunciata, il thread è ancora occupato dalla richiesta precedente");
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        Task<T?> guarded = RunGuardedAsync(work, operation);
        if (!ct.CanBeCanceled) return await guarded.ConfigureAwait(false);

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelled.TrySetResult(true)))
        {
            var first = await Task.WhenAny(guarded, cancelled.Task).ConfigureAwait(false);
            if (first == guarded) return await guarded.ConfigureAwait(false);
        }
        // il chiamante ha annullato: il lavoro prosegue in background sotto il cane da guardia, che libera la porta
        _log.Debug($"UIA: {operation} annullata dal chiamante");
        return null;
    }

    /// <summary>Possiede la porta: accoda il lavoro, aspetta al massimo WatchdogMs, poi sostituisce il thread.</summary>
    private async Task<T?> RunGuardedAsync<T>(Func<UiaSession, T?> work, string operation) where T : class
    {
        try
        {
            UiaWorker worker;
            lock (_workerLock)
            {
                worker = _worker;
            }

            var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = worker.TryEnqueue(session =>
            {
                if (session is null)
                {
                    completion.TrySetResult(null);
                    return;
                }
                try
                {
                    completion.TrySetResult(work(session));
                }
                catch (Exception ex)
                {
                    _log.Error($"UIA: {operation} ha lanciato un'eccezione non prevista", ex);
                    completion.TrySetResult(null);
                }
            });

            if (!queued)
            {
                ReplaceWorker(worker, $"UIA: thread non disponibile per {operation}; ne creo uno nuovo");
                return null;
            }

            var finished = await Task.WhenAny(completion.Task, Task.Delay(WatchdogMs)).ConfigureAwait(false);
            if (finished == completion.Task) return await completion.Task.ConfigureAwait(false);

            ReplaceWorker(worker, $"UIA: {operation} non ha risposto entro {WatchdogMs} ms; thread abbandonato e ricreato");
            return null;
        }
        catch (Exception ex)
        {
            _log.Error($"UIA: errore interno in {operation}", ex);
            return null;
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void ReplaceWorker(UiaWorker stale, string reason)
    {
        lock (_workerLock)
        {
            if (_disposed || !ReferenceEquals(_worker, stale)) return;
            _log.Warn(reason);
            stale.Abandon();
            _worker = new UiaWorker(_log, ++_generation);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UiaWorker worker;
        lock (_workerLock)
        {
            worker = _worker;
        }
        worker.Dispose();
    }
}
