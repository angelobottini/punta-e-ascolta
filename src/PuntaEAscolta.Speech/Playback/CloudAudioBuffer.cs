namespace PuntaEAscolta.Speech.Playback;

/// <summary>
/// Scarica in memoria lo stream di rete del fornitore cloud con un'attività propria ("pompa") e lo offre al lettore
/// come Stream in sola lettura. Serve a tre cose:
/// 1. sapere quando arrivano i primi byte PCM (tempo massimo per il primo audio);
/// 2. tenere una copia completa dell'audio per la cache (si salva solo se lo stream finisce normalmente);
/// 3. staccare il lettore senza chiudere la connessione: dopo uno Stop lo scaricamento può proseguire in sottofondo.
/// La lettura dalla rete usa un token proprio e mai quello del lettore: annullare una lettura di HttpClient chiude la connessione.
/// Una rete ferma senza byte per <c>stallTimeout</c> conta come errore (Timeout): il lettore riceve fine stream e non resta appeso.
/// </summary>
internal sealed class CloudAudioBuffer : Stream
{
    private const int ReadSize = 16 * 1024;
    private const int InitialCapacity = 64 * 1024;

    private readonly Stream _source;
    private readonly TimeSpan _stallTimeout;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly object _lock = new();
    private byte[] _data = new byte[InitialCapacity];
    private int _length;
    private int _readPosition;
    private bool _completed;
    private bool _aborted;
    private bool _consumerDetached;
    private bool _disposed;
    private Exception? _fault;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _pumpTask = Task.CompletedTask;

    public CloudAudioBuffer(Stream source, TimeSpan stallTimeout, TimeProvider time)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _stallTimeout = stallTimeout;
        _time = time;
    }

    /// <summary>Lo stream di rete è finito normalmente (letto fino in fondo, senza errori).</summary>
    public bool IsCompleted
    {
        get { lock (_lock) return _completed; }
    }

    /// <summary>Errore della rete durante lo scaricamento (null se nessuno o se lo scaricamento è stato interrotto da noi).</summary>
    public Exception? Fault
    {
        get { lock (_lock) return _fault; }
    }

    /// <summary>Byte scaricati finora.</summary>
    public int DownloadedBytes
    {
        get { lock (_lock) return _length; }
    }

    /// <summary>Avvia la pompa. Va chiamato una sola volta.</summary>
    public void Start()
    {
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <summary>Attende il primo blocco di dati, la fine dello stream o un errore.</summary>
    public async Task WaitForFirstDataAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_length > 0 || _completed || _fault is not null || _aborted) return;
                wait = _signal.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attende la fine dello scaricamento per al massimo <paramref name="limit"/>. Vero solo se lo stream è finito
    /// normalmente: allora <see cref="GetData"/> contiene l'audio completo.
    /// </summary>
    public async Task<bool> WaitForEndAsync(TimeSpan limit, CancellationToken ct)
    {
        Task pump;
        lock (_lock)
        {
            if (_completed) return true;
            if (_fault is not null || _aborted) return false;
            pump = _pumpTask;
        }
        try
        {
            await pump.WaitAsync(limit, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        lock (_lock) return _completed;
    }

    /// <summary>Audio scaricato finora. Completo solo se <see cref="IsCompleted"/>.</summary>
    public ReadOnlyMemory<byte> GetData()
    {
        lock (_lock) return new ReadOnlyMemory<byte>(_data, 0, _length);
    }

    /// <summary>Il lettore ha finito (o è stato fermato): le sue letture successive ricevono fine stream, la pompa prosegue.</summary>
    public void DetachConsumer()
    {
        lock (_lock) _consumerDetached = true;
        Signal();
    }

    /// <summary>Interrompe lo scaricamento (chiude la lettura di rete).</summary>
    public void Abort()
    {
        lock (_lock)
        {
            if (_completed || _aborted) return;
            _aborted = true;
        }
        try { _pumpCts.Cancel(); } catch (ObjectDisposedException) { /* già chiuso */ }
        Signal();
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[ReadSize];
        CancellationToken token = _pumpCts.Token;
        try
        {
            while (true)
            {
                int read = await _source.ReadAsync(buffer, token).AsTask().WaitAsync(_stallTimeout, _time, token).ConfigureAwait(false);
                if (read <= 0)
                {
                    lock (_lock) _completed = !_aborted;
                    return;
                }
                lock (_lock)
                {
                    if (_aborted) return;
                    if (_length + read > _data.Length)
                    {
                        Array.Resize(ref _data, Math.Max(_data.Length * 2, _length + read));
                    }
                    Buffer.BlockCopy(buffer, 0, _data, _length, read);
                    _length += read;
                }
                Signal();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Interruzione voluta (Abort o Dispose).
        }
        catch (TimeoutException ex)
        {
            lock (_lock) _fault ??= new SpeechProviderException(SpeechProviderReason.Timeout, "ElevenLabs: flusso audio fermo.", ex);
            try { _pumpCts.Cancel(); } catch (ObjectDisposedException) { /* già chiuso */ }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (!_aborted) _fault ??= ex;
            }
        }
        finally
        {
            Signal();
        }
    }

    private void Signal()
    {
        TaskCompletionSource previous;
        lock (_lock)
        {
            previous = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        previous.TrySetResult();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length == 0) return 0;
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_consumerDetached || _disposed) return 0;
                int available = _length - _readPosition;
                if (available > 0)
                {
                    int count = Math.Min(available, destination.Length);
                    _data.AsSpan(_readPosition, count).CopyTo(destination.Span);
                    _readPosition += count;
                    return count;
                }
                // Fine dati: stream finito, errore di rete (il lettore riceve fine stream) o scaricamento interrotto.
                if (_completed || _fault is not null || _aborted) return 0;
                wait = _signal.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    base.Dispose(disposing);
                    return;
                }
                _disposed = true;
            }
            Abort();
            // La pompa esce da sola (lettura annullata); lo stream di rete si chiude quando ha finito, mai durante una lettura.
            _ = _pumpTask.ContinueWith(static (_, state) =>
            {
                var self = (CloudAudioBuffer)state!;
                try { self._source.Dispose(); } catch (Exception) { /* connessione già chiusa */ }
                self._pumpCts.Dispose();
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Signal();
        }
        base.Dispose(disposing);
    }
}
