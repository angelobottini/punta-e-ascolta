using System.Collections.Concurrent;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Automation;

/// <summary>
/// Thread MTA dedicato, senza finestre, che possiede la UiaSession ed esegue in sequenza i lavori accodati.
/// Se un lavoro non torna, il thread viene abbandonato (resta in background) e UiaTextSource ne crea uno nuovo.
/// </summary>
internal sealed class UiaWorker : IDisposable
{
    private readonly ILog _log;
    private readonly BlockingCollection<Action<UiaSession?>> _queue = new();
    private readonly Thread _thread;
    private volatile bool _abandoned;

    public int Generation { get; }

    public UiaWorker(ILog log, int generation)
    {
        _log = log;
        Generation = generation;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "UiaThread-" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Accoda un lavoro; false se il thread è stato abbandonato o chiuso (il lavoro non verrà eseguito).</summary>
    public bool TryEnqueue(Action<UiaSession?> work)
    {
        if (_abandoned || _queue.IsAddingCompleted) return false;
        try
        {
            return _queue.TryAdd(work);
        }
        catch (InvalidOperationException)
        {
            // include ObjectDisposedException: coda chiusa o eliminata
            return false;
        }
    }

    /// <summary>Il thread non risponde: non riceverà altri lavori e terminerà da solo se e quando la chiamata bloccata tornerà.</summary>
    public void Abandon()
    {
        _abandoned = true;
        CompleteAddingSafe();
    }

    private void Loop()
    {
        UiaSession? session = null;
        try
        {
            session = new UiaSession();
            _log.Debug($"UIA: thread {Thread.CurrentThread.Name} avviato (CUIAutomation8 pronto)");
        }
        catch (Exception ex)
        {
            _log.Error("UIA: impossibile creare CUIAutomation8; il modulo restituirà sempre null", ex);
        }

        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                if (_abandoned) break;
                try
                {
                    work(session);
                }
                catch (Exception ex)
                {
                    // non deve mai uscire dal thread di lavoro
                    _log.Error("UIA: eccezione non prevista in un lavoro", ex);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // coda chiusa durante Dispose
        }
        finally
        {
            _log.Debug($"UIA: thread {Thread.CurrentThread.Name} terminato");
        }
    }

    private void CompleteAddingSafe()
    {
        try
        {
            if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        }
        catch (InvalidOperationException)
        {
            // include ObjectDisposedException: già chiusa
        }
    }

    public void Dispose()
    {
        CompleteAddingSafe();
        if (_thread.IsAlive && !_thread.Join(500))
            _log.Debug($"UIA: il thread {_thread.Name} non è terminato entro 500 ms; resta in background");
        _queue.Dispose();
    }
}
