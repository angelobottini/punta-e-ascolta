using System.Buffers;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Audio;

/// <summary>
/// Riproduzione di PCM grezzo con NAudio. Per ogni enunciato si apre un nuovo dispositivo WASAPI condiviso
/// (il predefinito, riletto ogni volta) e si alimenta un BufferedWaveProvider da un'attività di lettura.
/// La riproduzione parte dopo un piccolo pre-buffer o a fine stream; termina da sola quando i dati finiscono.
/// Stop() e l'annullamento fermano il dispositivo subito (decine di ms) e PlayAsync ritorna senza attendere
/// lo stream di rete. Se WASAPI fallisce si ripiega una volta su WaveOutEvent; se fallisce anche quello
/// PlayAsync termina senza eccezioni (l'errore va nel log).
/// </summary>
public sealed class NAudioPlayer : IAudioPlayer
{
    private readonly ILog _log;
    private readonly object _gate = new();
    private Session? _current;
    private bool _disposed;

    public NAudioPlayer(ILog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Vero dall'avvio di PlayAsync (pre-buffer incluso) fino alla fine dell'audio, allo Stop o all'errore.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _current is { IsActive: true };
            }
        }
    }

    public async Task PlayAsync(Stream pcm, PcmFormat format, double volume, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(format);

        if (!IsValid(format))
        {
            _log.Warn($"Riproduzione: formato PCM non valido ({format.SampleRate} Hz, {format.Channels} canali, {format.BitsPerSample} bit).");
            return;
        }

        Session session;
        Session? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                _log.Warn("Riproduzione richiesta dopo la chiusura del lettore: ignorata.");
                return;
            }

            previous = _current;
            session = new Session(_log);
            _current = session;
        }

        // Una nuova riproduzione interrompe la precedente.
        previous?.Stop();

        try
        {
            // Task.Run: nessun SynchronizationContext catturato da NAudio (gli eventi arrivano su thread propri).
            await Task.Run(() => session.RunAsync(pcm, format, volume, ct), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, session))
                {
                    _current = null;
                }
            }
        }
    }

    public void Stop()
    {
        Session? session;
        lock (_gate)
        {
            session = _current;
        }

        session?.Stop();
    }

    /// <summary>
    /// Paga in anticipo il costo del primo avvio del percorso audio (COM, ricampionatore, JIT), misurato in
    /// circa 1,5 s a freddo su ARM64: riproduce 20 ms di silenzio a volume zero su un dispositivo proprio.
    /// Facoltativo; da chiamare all'avvio dell'app fuori dal thread dell'interfaccia. Non lancia eccezioni.
    /// </summary>
    public Task WarmUpAsync(CancellationToken ct = default) => Task.Run(() => WarmUp(ct), CancellationToken.None);

    private void WarmUp(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var waveFormat = new WaveFormat(16000, 16, 1);
            var buffer = new BufferedWaveProvider(waveFormat, TimeSpan.FromSeconds(1)) { ReadFully = false };
            var output = new SampleToWaveProvider16(new VolumeSampleProvider(buffer.ToSampleProvider()) { Volume = 0f });
            // Nessun using sull'evento: il gestore gira sul thread di riproduzione e non deve mai lanciare.
            var done = new ManualResetEventSlim(false);
            using var device = new WasapiOut(AudioClientShareMode.Shared, true, Session.LatencyMs);
            device.PlaybackStopped += (_, _) => done.Set();
            device.Init(output);
            buffer.AddSamples(new byte[waveFormat.AverageBytesPerSecond / 50], 0, waveFormat.AverageBytesPerSecond / 50);
            device.Play();
            done.Wait(TimeSpan.FromSeconds(3), ct);
            device.Stop();
            _log.Info($"Audio: preriscaldamento del percorso di riproduzione in {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (OperationCanceledException)
        {
            // Avvio interrotto: nulla da fare.
        }
        catch (Exception ex)
        {
            _log.Warn($"Audio: preriscaldamento non riuscito ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    public void Dispose()
    {
        Session? session;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            session = _current;
            _current = null;
        }

        session?.Stop();
    }

    private static bool IsValid(PcmFormat format) =>
        format.SampleRate is >= 8000 and <= 192000
        && format.Channels is >= 1 and <= 8
        && format.BitsPerSample is 8 or 16 or 24 or 32;

    /// <summary>Una riproduzione: dispositivo, buffer, attività di lettura e stato di arresto.</summary>
    private sealed class Session
    {
        public const int LatencyMs = 100;
        private const int PreBufferMs = 120;
        private const int ChunkSize = 8 * 1024;
        private const int BackpressureDelayMs = 20;
        private static readonly TimeSpan BufferCapacity = TimeSpan.FromSeconds(30);

        private readonly ILog _log;
        private readonly object _lock = new();
        private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _readerCts = new();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        private IWavePlayer? _device;
        private IWaveProvider? _output;
        private bool _usingFallback;
        private bool _playRequested;
        private bool _started;
        private bool _stopRequested;

        public Session(ILog log)
        {
            _log = log;
        }

        public bool IsActive => !_finished.Task.IsCompleted;

        public async Task RunAsync(Stream pcm, PcmFormat format, double volume, CancellationToken ct)
        {
            if (_log.IsDebugEnabled)
            {
                _log.Debug($"Riproduzione: sessione avviata a {_clock.ElapsedMilliseconds} ms ({format.SampleRate} Hz, {format.Channels} canali, {format.BitsPerSample} bit).");
            }

            var waveFormat = new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
            var buffer = new BufferedWaveProvider(waveFormat, BufferCapacity)
            {
                DiscardOnBufferOverflow = false,
                // Finché lo stream è aperto un buco di rete diventa silenzio, non fine riproduzione.
                ReadFully = true,
            };
            var withVolume = new VolumeSampleProvider(buffer.ToSampleProvider())
            {
                Volume = (float)Math.Clamp(double.IsFinite(volume) ? volume : 1.0, 0.0, 1.0),
            };
            var output = new SampleToWaveProvider16(withVolume);

            lock (_lock)
            {
                _output = output;
            }

            if (ct.IsCancellationRequested)
            {
                Stop();
                return;
            }

            using CancellationTokenRegistration registration = ct.Register(static state => ((Session)state!).Stop(), this);

            // La lettura parte subito: il pre-buffer si riempie mentre il dispositivo si inizializza (30-100 ms misurati).
            _ = Task.Run(() => ReadLoopAsync(pcm, buffer, format), CancellationToken.None);

            // Anche l'apertura del dispositivo è concorrente: uno Stop durante Init completa PlayAsync subito
            // e il dispositivo, una volta pronto, viene scartato da Attach.
            _ = Task.Run(() =>
            {
                if (!TryOpenDevice())
                {
                    Stop();
                }
            }, CancellationToken.None);

            await _finished.Task.ConfigureAwait(false);
            CloseDevice();

            if (_log.IsDebugEnabled)
            {
                _log.Debug($"Riproduzione: sessione chiusa a {_clock.ElapsedMilliseconds} ms ({(_stopRequested ? "fermata" : "fine dati")}).");
            }
        }

        /// <summary>Arresto immediato: ferma il dispositivo, annulla la lettura e completa PlayAsync.</summary>
        public void Stop()
        {
            IWavePlayer? device;
            lock (_lock)
            {
                if (_stopRequested)
                {
                    return;
                }

                _stopRequested = true;
                device = _device;
            }

            // Fuori dal lock: Stop() di WasapiOut attende il thread di riproduzione, il cui ultimo atto
            // (PlaybackStopped) prende a sua volta il lock.
            try
            {
                _readerCts.Cancel();
            }
            catch (AggregateException ex)
            {
                _log.Warn($"Riproduzione: errore nell'annullamento della lettura ({ex.InnerException?.GetType().Name}).");
            }

            try
            {
                device?.Stop();
            }
            catch (Exception ex)
            {
                _log.Warn($"Riproduzione: errore nell'arresto del dispositivo ({ex.GetType().Name}).");
            }

            _finished.TrySetResult(true);
        }

        private bool TryOpenDevice()
        {
            IWaveProvider output;
            lock (_lock)
            {
                if (_stopRequested)
                {
                    return true;
                }

                output = _output!;
            }

            if (_log.IsDebugEnabled)
            {
                _log.Debug($"Riproduzione: apertura del dispositivo iniziata a {_clock.ElapsedMilliseconds} ms.");
            }

            try
            {
                IWavePlayer device = new WasapiOut(AudioClientShareMode.Shared, true, LatencyMs);
                Attach(device, output);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn($"Riproduzione: WASAPI non disponibile ({ex.GetType().Name}: {ex.Message}), ripiego su WaveOut.");
            }

            lock (_lock)
            {
                if (_stopRequested)
                {
                    return true;
                }
            }

            try
            {
                IWavePlayer device = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3 };
                Attach(device, output);
                lock (_lock)
                {
                    _usingFallback = true;
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Riproduzione: nessun dispositivo audio utilizzabile.", ex);
                return false;
            }
        }

        private void Attach(IWavePlayer device, IWaveProvider output)
        {
            try
            {
                device.PlaybackStopped += OnPlaybackStopped;
                device.Init(output);
            }
            catch
            {
                SafeDispose(device);
                throw;
            }

            bool discard;
            lock (_lock)
            {
                discard = _stopRequested;
                if (!discard)
                {
                    _device = device;
                }
            }

            if (discard)
            {
                // Arresto arrivato durante Init: il dispositivo non serve più.
                SafeDispose(device);
                return;
            }

            if (_log.IsDebugEnabled)
            {
                _log.Debug($"Riproduzione: dispositivo {device.GetType().Name} pronto a {_clock.ElapsedMilliseconds} ms.");
            }

            // Se la lettura ha già chiesto di partire (pre-buffer pieno o stream finito) si parte adesso.
            StartPlaybackIfNeeded();
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            bool stale;
            bool stopRequested;
            lock (_lock)
            {
                stale = !ReferenceEquals(sender, _device);
                stopRequested = _stopRequested;
            }

            if (stale)
            {
                return;
            }

            if (e.Exception is not null && !stopRequested)
            {
                // Mai eliminare il dispositivo dal suo stesso thread di riproduzione (Join su se stesso).
                Exception failure = e.Exception;
                _ = Task.Run(() => HandleDeviceFailure(failure));
                return;
            }

            if (_log.IsDebugEnabled)
            {
                _log.Debug($"Riproduzione: PlaybackStopped a {_clock.ElapsedMilliseconds} ms.");
            }

            _finished.TrySetResult(true);
        }

        /// <summary>Guasto del dispositivo durante la riproduzione: una volta si riparte con WaveOut sui dati rimasti nel buffer.</summary>
        private void HandleDeviceFailure(Exception failure)
        {
            IWavePlayer? old;
            IWaveProvider output;
            bool started;
            lock (_lock)
            {
                if (_stopRequested || _finished.Task.IsCompleted)
                {
                    return;
                }

                if (_usingFallback)
                {
                    _log.Error("Riproduzione: anche il dispositivo di ripiego si è fermato con errore.", failure);
                    _device = null;
                    old = null;
                    output = null!;
                    started = false;
                }
                else
                {
                    _log.Warn($"Riproduzione: il dispositivo WASAPI si è fermato con errore ({failure.GetType().Name}: {failure.Message}), ripiego su WaveOut.");
                    old = _device;
                    _device = null;
                    output = _output!;
                    started = _started;
                    _usingFallback = true;
                }
            }

            if (output is null)
            {
                _finished.TrySetResult(true);
                return;
            }

            SafeDispose(old);

            IWavePlayer device;
            try
            {
                device = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3 };
                Attach(device, output);
            }
            catch (Exception ex)
            {
                _log.Error("Riproduzione: impossibile aprire il dispositivo di ripiego.", ex);
                _finished.TrySetResult(true);
                return;
            }

            bool discard;
            lock (_lock)
            {
                discard = _stopRequested;
                if (!discard && started)
                {
                    try
                    {
                        device.Play();
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Riproduzione: impossibile avviare il dispositivo di ripiego.", ex);
                        discard = true;
                    }
                }

                if (discard)
                {
                    _device = null;
                }
            }

            if (discard)
            {
                SafeDispose(device);
                _finished.TrySetResult(true);
            }
        }

        /// <summary>Avvia la riproduzione se il dispositivo è pronto; altrimenti ricorda la richiesta per quando lo sarà.</summary>
        private void StartPlaybackIfNeeded(bool request = false)
        {
            lock (_lock)
            {
                if (request)
                {
                    _playRequested = true;
                }

                if (_started || _stopRequested || _device is null || !_playRequested)
                {
                    return;
                }

                _started = true;
                try
                {
                    _device.Play();
                    if (_log.IsDebugEnabled)
                    {
                        _log.Debug($"Riproduzione: avvio a {_clock.ElapsedMilliseconds} ms.");
                    }
                }
                catch (Exception ex)
                {
                    Exception failure = ex;
                    _ = Task.Run(() => HandleDeviceFailure(failure));
                }
            }
        }

        private async Task ReadLoopAsync(Stream pcm, BufferedWaveProvider buffer, PcmFormat format)
        {
            CancellationToken token = _readerCts.Token;
            byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
            try
            {
                int blockAlign = Math.Max(1, format.Channels * format.BitsPerSample / 8);
                int preBufferBytes = Math.Max(blockAlign, format.BytesPerSecond * PreBufferMs / 1000);
                int leftover = 0;

                while (!token.IsCancellationRequested)
                {
                    int read = await pcm.ReadAsync(chunk.AsMemory(leftover, ChunkSize - leftover), token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    int total = leftover + read;
                    // Mai passare byte spaiati: un campione spezzato produce rumore per tutto il resto del flusso.
                    int aligned = total - total % blockAlign;
                    if (aligned > 0)
                    {
                        while (buffer.BufferLength - buffer.BufferedBytes < aligned)
                        {
                            // Contropressione: la rete è più veloce del tempo reale e il buffer non deve traboccare.
                            await Task.Delay(BackpressureDelayMs, token).ConfigureAwait(false);
                        }

                        buffer.AddSamples(chunk, 0, aligned);
                    }

                    leftover = total - aligned;
                    if (leftover > 0)
                    {
                        Buffer.BlockCopy(chunk, aligned, chunk, 0, leftover);
                    }

                    if (buffer.BufferedBytes >= preBufferBytes)
                    {
                        StartPlaybackIfNeeded(request: true);
                    }
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                await FinishStreamAsync(buffer, format, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Arresto richiesto: nulla da fare.
            }
            catch (Exception ex)
            {
                _log.Error("Riproduzione: errore nella lettura dello stream audio; si riproduce quanto ricevuto.", ex);
                try
                {
                    await FinishStreamAsync(buffer, format, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Arresto richiesto durante la coda: nulla da fare.
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        /// <summary>Fine dei dati: il buffer si svuota e il dispositivo segnala PlaybackStopped; un guardiano forza l'arresto se non arriva.</summary>
        private async Task FinishStreamAsync(BufferedWaveProvider buffer, PcmFormat format, CancellationToken token)
        {
            // Da qui in avanti Read restituisce 0 quando il buffer è vuoto: il dispositivo termina da solo.
            buffer.ReadFully = false;
            StartPlaybackIfNeeded(request: true);

            int remainingMs = (int)Math.Min(buffer.BufferedBytes * 1000L / Math.Max(1, format.BytesPerSecond), 60_000);
            await Task.Delay(remainingMs + 3 * LatencyMs + 1000, token).ConfigureAwait(false);

            if (!_finished.Task.IsCompleted)
            {
                _log.Warn("Riproduzione: fine dati non segnalata dal dispositivo, arresto forzato.");
                Stop();
            }
        }

        private void CloseDevice()
        {
            IWavePlayer? device;
            lock (_lock)
            {
                device = _device;
                _device = null;
            }

            if (!_readerCts.IsCancellationRequested)
            {
                try
                {
                    // Fine naturale: la lettura (o il guardiano di fine dati) non ha più nulla da fare.
                    _readerCts.Cancel();
                }
                catch (AggregateException)
                {
                    // Le continuazioni annullate registrano da sé gli eventuali errori.
                }
            }

            if (device is not null)
            {
                // Dispose costa ~65 ms (misurati): fuori dal percorso critico, così PlayAsync ritorna subito
                // e la lettura successiva può aprire il proprio dispositivo senza attendere.
                _ = Task.Run(() => SafeDispose(device));
            }
        }

        private void SafeDispose(IWavePlayer? device)
        {
            if (device is null)
            {
                return;
            }

            try
            {
                device.PlaybackStopped -= OnPlaybackStopped;
                device.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn($"Riproduzione: errore nella chiusura del dispositivo ({ex.GetType().Name}).");
            }
        }
    }
}
