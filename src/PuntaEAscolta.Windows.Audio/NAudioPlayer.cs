using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Audio;

/// <summary>
/// Riproduzione di PCM grezzo con NAudio. Per ogni enunciato si apre un nuovo dispositivo WASAPI condiviso sul
/// dispositivo predefinito (riletto ogni volta) e lo si alimenta con un BufferedWaveProvider riempito da
/// un'attività di lettura a blocchi da 8 KB, con contropressione quando il buffer è quasi pieno.
/// La riproduzione parte dopo circa 120 ms di pre-buffer oppure a fine stream e termina da sola quando lo
/// stream è finito e il buffer si è svuotato. Stop() e l'annullamento fermano il dispositivo subito
/// (misurati: Stop ritorna in 2-5 ms, l'ultimo campione entra nel mix 30-40 ms dopo) e PlayAsync ritorna senza
/// attendere lo stream di rete: una nuova PlayAsync può partire immediatamente. Uno stream senza dati per 10 s
/// si considera finito. Se WASAPI fallisce si ripiega una volta su WaveOutEvent.
/// <para>
/// Contratto sulle eccezioni: PlayAsync NON lancia mai (salvo ArgumentNullException per argomenti null, errore di
/// programmazione). Fine naturale, Stop(), annullamento del token, formato non valido, stream illeggibile o
/// nessun dispositivo audio: in tutti i casi il Task si completa normalmente e gli errori vanno nel log.
/// Chi riproduce più pezzi in sequenza deve quindi controllare il proprio token dopo ogni PlayAsync.
/// </para>
/// </summary>
public sealed class NAudioPlayer : IAudioPlayer
{
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly AudioEngineKeepWarm _keepWarm;
    private Session? _current;
    private bool _disposed;

    public NAudioPlayer(ILog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _keepWarm = new AudioEngineKeepWarm(_log, TimeSpan.FromMinutes(3));
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

    /// <summary>
    /// Per quanto tempo dopo l'ultima lettura si tiene aperto (inizializzato, mai avviato, muto) uno stream WASAPI
    /// che mantiene pronto il motore audio: l'apertura del dispositivo scende da 200-300 ms a circa 20 ms.
    /// Predefinito 3 minuti; TimeSpan.Zero lo disattiva.
    /// </summary>
    public TimeSpan KeepWarmDuration
    {
        get => _keepWarm.Duration;
        set => _keepWarm.Duration = value;
    }

    /// <summary>Vero se lo stream di mantenimento è aperto (diagnostica).</summary>
    public bool IsEngineWarm => _keepWarm.IsActive;

    public async Task PlayAsync(Stream pcm, PcmFormat format, double volume, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(format);

        if (!IsValid(format))
        {
            _log.Warn($"Riproduzione: formato PCM non valido ({format.SampleRate} Hz, {format.Channels} canali, {format.BitsPerSample} bit).");
            return;
        }

        if (!pcm.CanRead)
        {
            _log.Warn("Riproduzione: lo stream audio non è leggibile.");
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
            session = new Session(_log, _keepWarm);
            _current = session;
        }

        // Una nuova riproduzione interrompe la precedente.
        previous?.Stop();

        try
        {
            // Task.Run: nessun SynchronizationContext catturato da NAudio (gli eventi arrivano su thread propri).
            await Task.Run(() => session.RunAsync(pcm, format, volume, ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Non dovrebbe mai accadere: RunAsync gestisce i propri errori. Per contratto PlayAsync non lancia.
            _log.Error("Riproduzione: errore inatteso.", ex);
            session.Stop();
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

    /// <summary>Ferma subito la riproduzione in corso (se c'è). Non blocca: ritorna in pochi millisecondi.</summary>
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
    /// Paga in anticipo il costo del primo avvio del percorso audio (COM, ricampionatore, JIT; a freddo fino a
    /// 1,5 s su ARM64) riproducendo 20 ms di silenzio su un dispositivo proprio, poi apre lo stream di mantenimento.
    /// Facoltativo; da chiamare all'avvio dell'app. Non lancia eccezioni e non tocca IsPlaying.
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
        MMDevice? endpoint = null;
        try
        {
            endpoint = Session.OpenDefaultEndpoint();
            string endpointId = endpoint.ID;
            var waveFormat = new WaveFormat(16000, 16, 1);
            var buffer = new BufferedWaveProvider(waveFormat, TimeSpan.FromSeconds(1)) { ReadFully = false };
            var output = new SampleToWaveProvider16(new VolumeSampleProvider(buffer.ToSampleProvider()) { Volume = 0f });
            // Nessun using sull'evento: il gestore gira sul thread di riproduzione e non deve mai lanciare.
            var done = new ManualResetEventSlim(false);
            using (var device = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, Session.LatencyMs))
            {
                device.PlaybackStopped += (_, _) => done.Set();
                device.Init(output);
                _keepWarm.Touch(endpointId);
                buffer.AddSamples(new byte[waveFormat.AverageBytesPerSecond / 50], 0, waveFormat.AverageBytesPerSecond / 50);
                device.Play();
                done.Wait(TimeSpan.FromSeconds(3), ct);
                device.Stop();
            }

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
        finally
        {
            try
            {
                endpoint?.Dispose();
            }
            catch (Exception)
            {
                // Rilascio del dispositivo: nulla da segnalare.
            }
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
        _keepWarm.Dispose();
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

        /// <summary>Silenzio aggiunto in coda allo stream: protegge gli ultimi ~30 ms dal taglio a fine naturale (margine 20 ms).</summary>
        private const int EndPaddingMs = 50;
        private static readonly TimeSpan BufferCapacity = TimeSpan.FromSeconds(10);

        /// <summary>Oltre questo tempo senza dati da uno stream ancora aperto si considera finito (rete bloccata).</summary>
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);

        /// <summary>Attesa massima perché il dispositivo parta dopo la fine dei dati (apertura a freddo inclusa).</summary>
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

        private readonly ILog _log;
        private readonly AudioEngineKeepWarm _keepWarm;
        private readonly object _lock = new();
        private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _readerCts = new();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        private IWavePlayer? _device;
        private MMDevice? _endpoint;
        private IWaveProvider? _output;
        private bool _usingFallback;
        private bool _playRequested;
        private bool _playing;
        private bool _stopRequested;

        public Session(ILog log, AudioEngineKeepWarm keepWarm)
        {
            _log = log;
            _keepWarm = keepWarm;
        }

        public bool IsActive => !_finished.Task.IsCompleted;

        public static MMDevice OpenDefaultEndpoint()
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }

        public async Task RunAsync(Stream pcm, PcmFormat format, double volume, CancellationToken ct)
        {
            if (_log.IsDebugEnabled)
            {
                _log.Debug($"Riproduzione: sessione avviata ({format.SampleRate} Hz, {format.Channels} canali, {format.BitsPerSample} bit).");
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

            // La lettura parte subito: il pre-buffer si riempie mentre il dispositivo si inizializza.
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
            _keepWarm.Renew();

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

            try
            {
                _readerCts.Cancel();
            }
            catch (AggregateException ex)
            {
                _log.Warn($"Riproduzione: errore nell'annullamento della lettura ({ex.InnerException?.GetType().Name}).");
            }

            // Fuori dal lock: Stop() del dispositivo può attendere il suo thread, il cui ultimo atto
            // (PlaybackStopped) prende a sua volta il lock. Con NAudio 2.4 ritorna in 1-10 ms.
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

            MMDevice? endpoint = null;
            try
            {
                endpoint = OpenDefaultEndpoint();
                string endpointId = endpoint.ID;
                IWavePlayer device = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, LatencyMs);
                Attach(device, output, endpoint);
                endpoint = null; // ora appartiene alla sessione
                _keepWarm.Touch(endpointId);
                return true;
            }
            catch (Exception ex)
            {
                DisposeEndpoint(endpoint);
                _log.Warn($"Riproduzione: WASAPI non disponibile ({ex.GetType().Name}: {ex.Message}), ripiego su WaveOut.");
            }

            lock (_lock)
            {
                if (_stopRequested)
                {
                    return true;
                }

                _usingFallback = true;
            }

            try
            {
                Attach(CreateFallbackDevice(), output, null);
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Riproduzione: nessun dispositivo audio utilizzabile.", ex);
                return false;
            }
        }

        private static IWavePlayer CreateFallbackDevice() => new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3 };

        /// <summary>Inizializza il dispositivo e lo rende quello della sessione; se nel frattempo è arrivato uno Stop lo scarta.</summary>
        private void Attach(IWavePlayer device, IWaveProvider output, MMDevice? endpoint)
        {
            try
            {
                device.PlaybackStopped += OnPlaybackStopped;
                device.Init(output);
            }
            catch
            {
                SafeDispose(device, null);
                throw;
            }

            bool discard;
            lock (_lock)
            {
                discard = _stopRequested;
                if (!discard)
                {
                    _device = device;
                    _endpoint = endpoint;
                }
            }

            if (discard)
            {
                // Arresto arrivato durante Init: il dispositivo non serve più.
                SafeDispose(device, endpoint);
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
            try
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
            catch (Exception ex)
            {
                // Thread di riproduzione di NAudio: nessuna eccezione deve uscire.
                _log.Error("Riproduzione: errore nella gestione della fine.", ex);
                _finished.TrySetResult(true);
            }
        }

        /// <summary>Guasto del dispositivo durante la riproduzione: una sola volta si riparte con WaveOut sui dati rimasti nel buffer.</summary>
        private void HandleDeviceFailure(Exception failure)
        {
            IWavePlayer? oldDevice;
            MMDevice? oldEndpoint;
            IWaveProvider? output;
            bool wasPlaying;
            lock (_lock)
            {
                if (_stopRequested || _finished.Task.IsCompleted)
                {
                    return;
                }

                oldDevice = _device;
                oldEndpoint = _endpoint;
                _device = null;
                _endpoint = null;
                wasPlaying = _playing;
                _playing = false;
                output = _usingFallback ? null : _output;
                _usingFallback = true;
            }

            SafeDispose(oldDevice, oldEndpoint);

            if (output is null)
            {
                _log.Error("Riproduzione: anche il dispositivo di ripiego si è fermato con errore.", failure);
                _finished.TrySetResult(true);
                return;
            }

            _log.Warn($"Riproduzione: il dispositivo si è fermato con errore ({failure.GetType().Name}: {failure.Message}), ripiego su WaveOut.");
            try
            {
                if (wasPlaying)
                {
                    lock (_lock)
                    {
                        _playRequested = true;
                    }
                }

                Attach(CreateFallbackDevice(), output, null);
            }
            catch (Exception ex)
            {
                _log.Error("Riproduzione: impossibile aprire il dispositivo di ripiego.", ex);
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

                if (_playing || _stopRequested || _device is null || !_playRequested)
                {
                    return;
                }

                _playing = true;
                try
                {
                    // Dentro il lock: uno Stop concorrente non può arrivare fra il controllo e Play() (che non
                    // attende il thread di riproduzione e ritorna in circa 1 ms).
                    _device.Play();
                    _started.TrySetResult(true);
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
            // Array privato e non del pool: una lettura abbandonata per blocco della rete può ancora scriverci.
            byte[] chunk = new byte[ChunkSize];
            try
            {
                int blockAlign = Math.Max(1, format.Channels * format.BitsPerSample / 8);
                int preBufferBytes = Math.Max(blockAlign, format.BytesPerSecond * PreBufferMs / 1000);
                int leftover = 0;
                long totalQueued = 0;

                while (!token.IsCancellationRequested)
                {
                    int read = await ReadWithStallTimeoutAsync(pcm, chunk.AsMemory(leftover, ChunkSize - leftover), token).ConfigureAwait(false);
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
                        totalQueued += aligned;
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

                token.ThrowIfCancellationRequested();
                if (totalQueued == 0)
                {
                    // Stream vuoto: niente da suonare, si chiude subito senza attendere il dispositivo.
                    Stop();
                    return;
                }

                await FinishStreamAsync(buffer, format, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Arresto richiesto: nulla da fare.
            }
            catch (Exception ex) when (token.IsCancellationRequested)
            {
                // Dopo lo Stop il chiamante può chiudere lo stream mentre una lettura è in corso: non è un errore.
                if (_log.IsDebugEnabled)
                {
                    _log.Debug($"Riproduzione: lettura interrotta dopo l'arresto ({ex.GetType().Name}).");
                }
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
                catch (Exception inner)
                {
                    _log.Error("Riproduzione: errore nella chiusura dello stream audio.", inner);
                    Stop();
                }
            }
        }

        /// <summary>Legge un blocco; se lo stream non consegna nulla per StallTimeout si considera finito.</summary>
        private async Task<int> ReadWithStallTimeoutAsync(Stream pcm, Memory<byte> destination, CancellationToken token)
        {
            ValueTask<int> pending = pcm.ReadAsync(destination, token);
            if (pending.IsCompletedSuccessfully)
            {
                return pending.Result;
            }

            Task<int> read = pending.AsTask();
            try
            {
                return await read.WaitAsync(StallTimeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Warn($"Riproduzione: nessun dato audio da {StallTimeout.TotalSeconds:0} s, si considera finito lo stream.");
                ObserveAbandoned(read);
                return 0;
            }
            catch (OperationCanceledException)
            {
                ObserveAbandoned(read);
                throw;
            }
        }

        /// <summary>Una lettura abbandonata può fallire più tardi (stream chiuso dal chiamante): l'errore va osservato.</summary>
        private static void ObserveAbandoned(Task<int> read)
        {
            _ = read.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>Fine dei dati: il buffer si svuota e il dispositivo segnala PlaybackStopped; un guardiano forza l'arresto se non arriva.</summary>
        private async Task FinishStreamAsync(BufferedWaveProvider buffer, PcmFormat format, CancellationToken token)
        {
            // Alla fine naturale NAudio ferma lo stream WASAPI prima che gli ultimi ~30 ms siano suonati (misurato con
            // toni a bordi netti: 385 ms uditi su 400). Un breve silenzio in coda fa cadere il taglio sul silenzio.
            int blockAlign = Math.Max(1, format.Channels * format.BitsPerSample / 8);
            int padBytes = format.BytesPerSecond * EndPaddingMs / 1000 / blockAlign * blockAlign;
            while (buffer.BufferLength - buffer.BufferedBytes < padBytes)
            {
                await Task.Delay(BackpressureDelayMs, token).ConfigureAwait(false);
            }

            byte[] padding = new byte[padBytes];
            if (format.BitsPerSample == 8)
            {
                // PCM a 8 bit è senza segno: il silenzio vale 128.
                Array.Fill(padding, (byte)0x80);
            }

            buffer.AddSamples(padding, 0, padding.Length);

            // Da qui in avanti Read restituisce 0 quando il buffer è vuoto: il dispositivo termina da solo.
            buffer.ReadFully = false;
            StartPlaybackIfNeeded(request: true);

            // Il guardiano conta dall'avvio effettivo: l'apertura a freddo del dispositivo può durare più dell'audio.
            try
            {
                await _started.Task.WaitAsync(StartTimeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (!_finished.Task.IsCompleted)
                {
                    _log.Warn("Riproduzione: il dispositivo non è partito, arresto forzato.");
                    Stop();
                }

                return;
            }

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
            MMDevice? endpoint;
            lock (_lock)
            {
                device = _device;
                endpoint = _endpoint;
                _device = null;
                _endpoint = null;
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

            if (device is not null || endpoint is not null)
            {
                // Dispose costa fino a ~70 ms: fuori dal percorso critico, così PlayAsync ritorna subito
                // e la lettura successiva può aprire il proprio dispositivo senza attendere.
                _ = Task.Run(() => SafeDispose(device, endpoint));
            }
        }

        private void SafeDispose(IWavePlayer? device, MMDevice? endpoint)
        {
            if (device is not null)
            {
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

            DisposeEndpoint(endpoint);
        }

        private void DisposeEndpoint(MMDevice? endpoint)
        {
            try
            {
                endpoint?.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn($"Riproduzione: errore nel rilascio del dispositivo ({ex.GetType().Name}).");
            }
        }
    }
}
