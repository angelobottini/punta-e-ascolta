using System.Collections.Concurrent;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Speech.Cache;
using PuntaEAscolta.Speech.ElevenLabs;
using PuntaEAscolta.Speech.Playback;

namespace PuntaEAscolta.Speech;

/// <summary>
/// Servizio vocale: sceglie il fornitore, usa la cache, spezza la lettura nei pezzi ricevuti e ripiega sulla voce locale.
/// <list type="bullet">
/// <item>Fornitore: <c>Windows</c> sempre locale; <c>Auto</c> ed <c>ElevenLabs</c> usano il cloud se configurato e non sospeso.
/// I messaggi <see cref="SpeechKind.System"/> e, con <c>DocumentsWithLocalVoiceOnly</c>, i testi <c>Sensitive</c> vanno sempre in locale.</item>
/// <item>Cache (solo ElevenLabs, chiave da <see cref="ElevenLabsSynthesizer.DescribeForCache"/>): consultata per prima; un hit parte in pochi ms,
/// anche con il cloud sospeso.</item>
/// <item>Cloud: tempo massimo per i primi byte PCM (<c>FirstAudioTimeoutLabelMs</c> / <c>FirstAudioTimeoutSentenceMs</c>), poi ripiego immediato
/// sulla voce locale. L'audio suonato viene copiato e salvato in cache solo se lo stream finisce normalmente; dopo uno Stop lo
/// scaricamento prosegue in sottofondo fino a 15 s, altrimenti la copia viene scartata.</item>
/// <item>Interruttore automatico: vedi <see cref="CloudCircuitBreaker"/>.</item>
/// <item>Pezzi (<see cref="SpeechRequest.Chunks"/>) in ordine, preparando in anticipo solo il successivo.</item>
/// <item><see cref="Stop"/> immediato e idempotente; una nuova lettura annulla la precedente. Mai eccezioni verso il chiamante,
/// salvo OperationCanceledException quando è il suo token a essere annullato.</item>
/// </list>
/// </summary>
public sealed class SpeechService : ISpeechServiceWithOutcome
{
    /// <summary>Dopo uno Stop, tempo concesso allo scaricamento in sottofondo per completare l'audio e metterlo in cache.</summary>
    internal static readonly TimeSpan BackgroundCompletionLimit = TimeSpan.FromSeconds(15);

    /// <summary>Rete ferma senza byte per questo tempo a metà di uno stream: errore, il lettore riceve fine stream.</summary>
    internal static readonly TimeSpan StreamStallTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Attesa massima perché la lettura precedente si fermi prima di iniziare la nuova.</summary>
    internal static readonly TimeSpan PreviousStopWait = TimeSpan.FromSeconds(2);

    internal const int MinFirstAudioTimeoutMs = 100;
    internal const int MaxFirstAudioTimeoutMs = 30_000;

    private readonly ISpeechSynthesizer? _cloud;
    private readonly ISpeechSynthesizer _local;
    private readonly IAudioPlayer _player;
    private readonly SpeechCache _cache;
    private readonly ISettingsStore _settings;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly CloudCircuitBreaker _breaker;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly object _eventGate = new();
    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private Utterance? _current;
    private Task _lastDone = Task.CompletedTask;
    private bool _raisedSpeaking;
    private bool _disposed;

    public SpeechService(ISpeechSynthesizer? cloud, ISpeechSynthesizer local, IAudioPlayer player, SpeechCache cache,
        ISettingsStore settings, ILog log)
        : this(cloud, local, player, cache, settings, log, TimeProvider.System)
    {
    }

    /// <summary>Come il costruttore principale, con l'orologio iniettabile (prove).</summary>
    public SpeechService(ISpeechSynthesizer? cloud, ISpeechSynthesizer local, IAudioPlayer player, SpeechCache cache,
        ISettingsStore settings, ILog log, TimeProvider time)
    {
        _cloud = cloud;
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? NullLog.Instance;
        _time = time ?? TimeProvider.System;
        _breaker = new CloudCircuitBreaker(_time, _log);
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Vero dall'inizio di una lettura (attesa del primo audio compresa) fino alla sua fine o allo Stop.</summary>
    public bool IsSpeaking
    {
        get { lock (_sync) return _current is not null; }
    }

    public event Action<bool>? SpeakingChanged;

    /// <summary>Fine della sospensione del cloud (DateTimeOffset.MaxValue = fino al cambio delle impostazioni); null se disponibile.</summary>
    public DateTimeOffset? CloudSuspendedUntil => _breaker.SuspendedUntil;

    public Task SpeakAsync(SpeechRequest request, CancellationToken ct) => SpeakWithOutcomeAsync(request, ct);

    public async Task<SpeechOutcome> SpeakWithOutcomeAsync(SpeechRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        List<string> texts = SelectChunks(request);
        if (texts.Count == 0)
        {
            Stop(); // una lettura vuota interrompe comunque quella in corso
            return SpeechOutcome.Completed;
        }

        Utterance utterance;
        Utterance? previous;
        Task previousDone;
        lock (_sync)
        {
            if (_disposed) return SpeechOutcome.Stopped;
            utterance = new Utterance(CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token));
            previous = _current;
            _current = utterance;
            previousDone = _lastDone;
            _lastDone = utterance.Done;
        }
        previous?.Cancel(superseded: true);
        RaiseSpeakingIfChanged();

        if (_log.IsDebugEnabled)
        {
            _log.Debug($"Voce: lettura {request.Kind} in {texts.Count} pezzi, lingua {request.LanguageHint ?? "-"}: \"{request.Text}\"");
        }

        SpeechOutcome outcome;
        CancellationToken token = utterance.Token;
        try
        {
            await WaitForPreviousAsync(previousDone, token).ConfigureAwait(false);
            outcome = await RunAsync(texts, request, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = utterance.Superseded ? SpeechOutcome.Superseded : SpeechOutcome.Stopped;
        }
        catch (Exception ex)
        {
            _log.Error("Voce: errore imprevisto durante la lettura.", ex);
            outcome = SpeechOutcome.Failed;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_current, utterance)) _current = null;
            }
            utterance.Finish();
            RaiseSpeakingIfChanged();
        }

        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        return outcome;
    }

    /// <summary>Ferma subito la voce e la lettura in corso. Idempotente, mai eccezioni.</summary>
    public void Stop()
    {
        Utterance? utterance;
        lock (_sync)
        {
            utterance = _current;
            _current = null;
        }
        utterance?.Cancel(superseded: false);
        try
        {
            _player.Stop();
        }
        catch (Exception ex)
        {
            _log.Warn($"Voce: arresto del lettore non riuscito ({ex.GetType().Name}: {ex.Message}).");
        }
        if (utterance is not null) RaiseSpeakingIfChanged();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _settings.Changed -= OnSettingsChanged;
        Stop();
        try { _lifetime.Cancel(); } catch (AggregateException ex) { _log.Warn($"Voce: chiusura ({ex.GetType().Name})."); }
    }

    /// <summary>Per le prove: attende gli scaricamenti in sottofondo e i salvataggi in cache.</summary>
    internal Task WaitForBackgroundAsync() => Task.WhenAll(_background.Keys);

    // -----------------------------------------------------------------------------------------------------------------
    // Svolgimento di una lettura
    // -----------------------------------------------------------------------------------------------------------------

    private async Task<SpeechOutcome> RunAsync(List<string> texts, SpeechRequest request, CancellationToken token)
    {
        int played = 0;
        PendingChunk? next = BeginChunk(texts[0], request, 0, texts.Count, token);
        try
        {
            for (int i = 0; i < texts.Count; i++)
            {
                PendingChunk current = next!;
                next = null;

                PreparedAudio? audio;
                try
                {
                    audio = await ObtainAsync(current, token).ConfigureAwait(false);
                }
                catch
                {
                    current.Abandon();
                    throw;
                }

                // Preparazione anticipata di UN solo pezzo: parte appena il pezzo corrente ha il suo audio.
                if (i + 1 < texts.Count) next = BeginChunk(texts[i + 1], request, i + 1, texts.Count, token);

                if (audio is null)
                {
                    current.Abandon();
                    continue;
                }
                current.Release();
                await PlayAsync(audio, token).ConfigureAwait(false);
                played++;
            }
        }
        finally
        {
            next?.Abandon();
        }
        return played > 0 ? SpeechOutcome.Completed : SpeechOutcome.Failed;
    }

    private PendingChunk BeginChunk(string text, SpeechRequest request, int index, int count, CancellationToken token)
    {
        var chunkRequest = new SpeechRequest(text, request.Kind, request.LanguageHint, request.Sensitive);
        var pending = new PendingChunk(chunkRequest, index, count);
        SpeechSettings speech = _settings.Current.Speech;

        if (ShouldUseCloud(chunkRequest, speech))
        {
            pending.Key = speech.CacheEnabled ? TryDescribeForCache(chunkRequest) : null;
            if (pending.Key is not null && _cache.TryOpen(pending.Key, out var cached, out var format))
            {
                pending.Cached = new PreparedAudio(cached, format, AudioOrigin.Cache);
                return pending;
            }
            if (IsCloudUsable())
            {
                pending.StartCloud(ct => PrepareCloudAsync(chunkRequest, pending.Key, ct), token);
                return pending;
            }
        }
        pending.LocalTask = PrepareLocalAsync(chunkRequest, token);
        return pending;
    }

    private async Task<PreparedAudio?> ObtainAsync(PendingChunk pending, CancellationToken token)
    {
        long start = _time.GetTimestamp();
        PreparedAudio? audio = null;

        if (pending.Cached is { } cached)
        {
            pending.Cached = null;
            audio = cached;
        }
        else if (pending.CloudTask is { } cloudTask)
        {
            int timeoutMs = FirstAudioTimeoutMs(pending.Request.Kind);
            try
            {
                audio = await cloudTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), _time, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _breaker.RecordFailure(SpeechProviderReason.Timeout);
                _log.Warn($"Voce: nessun audio da ElevenLabs entro {timeoutMs} ms, uso la voce locale.");
                pending.AbandonCloud();
            }
        }

        if (audio is null)
        {
            pending.LocalTask ??= PrepareLocalAsync(pending.Request, token);
            audio = await pending.LocalTask.ConfigureAwait(false);
        }

        if (audio is not null && _log.IsDebugEnabled)
        {
            _log.Debug($"Voce: pezzo {pending.Index + 1}/{pending.Count} da {Describe(audio.Origin)}, primo audio in {_time.GetElapsedTime(start).TotalMilliseconds:F0} ms.");
        }
        return audio;
    }

    private async Task<PreparedAudio?> PrepareCloudAsync(SpeechRequest request, SpeechCacheKey? key, CancellationToken ct)
    {
        CloudAudioBuffer? buffer = null;
        SpeechAudio? audio = null;
        try
        {
            audio = await _cloud!.SynthesizeAsync(request, ct).ConfigureAwait(false);
            buffer = new CloudAudioBuffer(audio.Pcm, StreamStallTimeout, _time);
            buffer.Start();
            await buffer.WaitForFirstDataAsync(ct).ConfigureAwait(false);

            if (buffer.DownloadedBytes == 0)
            {
                SpeechProviderReason reason = buffer.Fault is SpeechProviderException spe ? spe.Reason
                    : buffer.Fault is not null ? SpeechProviderReason.Network
                    : SpeechProviderReason.Server;
                _breaker.RecordFailure(reason);
                _log.Warn($"Voce: ElevenLabs non ha inviato audio ({reason}), uso la voce locale.");
                buffer.Dispose();
                return null;
            }

            _breaker.RecordSuccess();
            return new PreparedAudio(buffer, audio.Format, AudioOrigin.Cloud, buffer, key);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DisposeQuietly(buffer, audio);
            throw;
        }
        catch (SpeechProviderException ex)
        {
            DisposeQuietly(buffer, audio);
            _breaker.RecordFailure(ex.Reason);
            _log.Info($"Voce: ElevenLabs non disponibile per questa lettura ({ex.Reason}), uso la voce locale.");
            return null;
        }
        catch (Exception ex)
        {
            DisposeQuietly(buffer, audio);
            _breaker.RecordFailure(SpeechProviderReason.Network);
            _log.Warn($"Voce: errore imprevisto del fornitore cloud ({ex.GetType().Name}: {ex.Message}), uso la voce locale.");
            return null;
        }
    }

    private async Task<PreparedAudio?> PrepareLocalAsync(SpeechRequest request, CancellationToken ct)
    {
        try
        {
            SpeechAudio audio = await _local.SynthesizeAsync(request, ct).ConfigureAwait(false);
            return new PreparedAudio(audio.Pcm, audio.Format, AudioOrigin.Local);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Voce: la voce locale non è riuscita a leggere il testo.", ex);
            return null;
        }
    }

    /// <summary>Suona l'audio preparato. Solleva OperationCanceledException se la lettura viene fermata o sostituita.</summary>
    private async Task PlayAsync(PreparedAudio audio, CancellationToken token)
    {
        try
        {
            await _player.PlayAsync(audio.Stream, audio.Format, CurrentVolume(), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Fermata: si gestisce sotto.
        }
        catch (Exception ex)
        {
            _log.Error("Voce: riproduzione non riuscita.", ex);
        }
        finally
        {
            FinishAudio(audio);
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Dopo la riproduzione: cache se l'audio cloud è completo, scaricamento in sottofondo se è stato fermato a metà.</summary>
    private void FinishAudio(PreparedAudio audio)
    {
        if (audio.Buffer is not { } buffer)
        {
            audio.Dispose();
            return;
        }

        buffer.DetachConsumer();
        SpeechCacheKey? key = audio.Key;
        PcmFormat format = audio.Format;

        if (buffer.IsCompleted)
        {
            if (key is null)
            {
                buffer.Dispose();
                return;
            }
            RunInBackground(() =>
            {
                try { StoreInCache(key, format, buffer.GetData()); }
                finally { buffer.Dispose(); }
                return Task.CompletedTask;
            });
            return;
        }

        if (buffer.Fault is { } fault)
        {
            SpeechProviderReason reason = fault is SpeechProviderException spe ? spe.Reason : SpeechProviderReason.Network;
            _breaker.RecordFailure(reason);
            _log.Warn($"Voce: flusso ElevenLabs interrotto ({reason}); l'audio non entra in cache.");
            buffer.Dispose();
            return;
        }

        if (key is null)
        {
            buffer.Abort();
            buffer.Dispose();
            return;
        }

        // Fermata a metà: lo scaricamento prosegue in sottofondo per non sprecare i crediti già spesi.
        RunInBackground(async () =>
        {
            try
            {
                bool complete = await buffer.WaitForEndAsync(BackgroundCompletionLimit, _lifetime.Token).ConfigureAwait(false);
                if (complete)
                {
                    StoreInCache(key, format, buffer.GetData());
                }
                else if (_log.IsDebugEnabled)
                {
                    _log.Debug("Voce: scaricamento in sottofondo non completato, audio parziale scartato.");
                }
            }
            catch (OperationCanceledException)
            {
                // Servizio chiuso.
            }
            finally
            {
                buffer.Dispose();
            }
        });
    }

    private void StoreInCache(SpeechCacheKey key, PcmFormat format, ReadOnlyMemory<byte> pcm)
    {
        if (!_settings.Current.Speech.CacheEnabled) return;
        if (_cache.Store(key, format, pcm) && _log.IsDebugEnabled)
        {
            _log.Debug($"Voce: audio salvato in cache ({pcm.Length / 1024} KB).");
        }
    }

    private void RunInBackground(Func<Task> work)
    {
        Task task = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn($"Voce: attività in sottofondo non riuscita ({ex.GetType().Name}: {ex.Message}).");
            }
        });
        _background[task] = 0;
        _ = task.ContinueWith(t => _background.TryRemove(t, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WaitForPreviousAsync(Task previousDone, CancellationToken token)
    {
        if (previousDone.IsCompleted) return;
        Task finished = await Task.WhenAny(previousDone, Task.Delay(PreviousStopWait, _time, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(finished, previousDone))
        {
            _log.Warn("Voce: la lettura precedente non si è fermata in tempo, forzo l'arresto del lettore.");
            try { _player.Stop(); } catch (Exception) { /* registrato altrove */ }
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Regole
    // -----------------------------------------------------------------------------------------------------------------

    private bool ShouldUseCloud(SpeechRequest request, SpeechSettings speech)
    {
        if (_cloud is null) return false;
        if (request.Kind == SpeechKind.System) return false;
        if (request.Sensitive && speech.DocumentsWithLocalVoiceOnly) return false;
        return speech.Provider != SpeechProviderKind.Windows;
    }

    private bool IsCloudUsable()
    {
        if (!_breaker.IsAvailable) return false;
        try
        {
            return _cloud!.IsConfigured;
        }
        catch (Exception ex)
        {
            _log.Warn($"Voce: stato del fornitore cloud non leggibile ({ex.GetType().Name}).");
            return false;
        }
    }

    private SpeechCacheKey? TryDescribeForCache(SpeechRequest request)
    {
        if (_cloud is not ElevenLabsSynthesizer elevenLabs) return null;
        try
        {
            return elevenLabs.DescribeForCache(request);
        }
        catch (Exception ex)
        {
            _log.Warn($"Voce: chiave di cache non calcolabile ({ex.GetType().Name}).");
            return null;
        }
    }

    private int FirstAudioTimeoutMs(SpeechKind kind)
    {
        SpeechSettings speech = _settings.Current.Speech;
        int ms = kind == SpeechKind.Label ? speech.FirstAudioTimeoutLabelMs : speech.FirstAudioTimeoutSentenceMs;
        return Math.Clamp(ms, MinFirstAudioTimeoutMs, MaxFirstAudioTimeoutMs);
    }

    private double CurrentVolume()
    {
        double volume = _settings.Current.Speech.Volume;
        return double.IsFinite(volume) ? Math.Clamp(volume, 0.0, 1.0) : 1.0;
    }

    private static List<string> SelectChunks(SpeechRequest request)
    {
        var texts = new List<string>();
        if (request.Chunks is { Count: > 0 } chunks)
        {
            foreach (string chunk in chunks)
            {
                if (!string.IsNullOrWhiteSpace(chunk)) texts.Add(chunk.Trim());
            }
        }
        if (texts.Count == 0 && !string.IsNullOrWhiteSpace(request.Text)) texts.Add(request.Text.Trim());
        return texts;
    }

    private void OnSettingsChanged(AppSettings _) => _breaker.Reset();

    private void RaiseSpeakingIfChanged()
    {
        lock (_eventGate)
        {
            bool speaking = IsSpeaking;
            if (speaking == _raisedSpeaking) return;
            _raisedSpeaking = speaking;
            try
            {
                SpeakingChanged?.Invoke(speaking);
            }
            catch (Exception ex)
            {
                _log.Error("Voce: errore in un gestore di SpeakingChanged.", ex);
            }
        }
    }

    private static string Describe(AudioOrigin origin) => origin switch
    {
        AudioOrigin.Cache => "cache",
        AudioOrigin.Cloud => "ElevenLabs",
        _ => "voce locale"
    };

    private static void DisposeQuietly(CloudAudioBuffer? buffer, SpeechAudio? audio)
    {
        try
        {
            if (buffer is not null) buffer.Dispose();
            else audio?.Pcm.Dispose();
        }
        catch (Exception)
        {
            // Connessione già chiusa.
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Tipi interni
    // -----------------------------------------------------------------------------------------------------------------

    private enum AudioOrigin { Cache, Cloud, Local }

    private sealed class PreparedAudio(Stream stream, PcmFormat format, AudioOrigin origin, CloudAudioBuffer? buffer = null, SpeechCacheKey? key = null)
    {
        public Stream Stream { get; } = stream;
        public PcmFormat Format { get; } = format;
        public AudioOrigin Origin { get; } = origin;
        public CloudAudioBuffer? Buffer { get; } = buffer;
        public SpeechCacheKey? Key { get; } = key;

        public void Dispose()
        {
            try { Stream.Dispose(); } catch (Exception) { /* già chiuso */ }
        }
    }

    /// <summary>Un pezzo in preparazione: audio in cache già aperto, richiesta cloud in corso o sintesi locale in corso.</summary>
    private sealed class PendingChunk(SpeechRequest request, int index, int count)
    {
        private CancellationTokenSource? _cloudCts;

        public SpeechRequest Request { get; } = request;
        public int Index { get; } = index;
        public int Count { get; } = count;
        public SpeechCacheKey? Key { get; set; }
        public PreparedAudio? Cached { get; set; }
        public Task<PreparedAudio?>? CloudTask { get; private set; }
        public Task<PreparedAudio?>? LocalTask { get; set; }

        public void StartCloud(Func<CancellationToken, Task<PreparedAudio?>> prepare, CancellationToken utteranceToken)
        {
            _cloudCts = CancellationTokenSource.CreateLinkedTokenSource(utteranceToken);
            CloudTask = prepare(_cloudCts.Token);
        }

        /// <summary>Rinuncia alla richiesta cloud (tempo scaduto o lettura finita): la annulla e chiude l'eventuale audio arrivato tardi.</summary>
        public void AbandonCloud()
        {
            if (CloudTask is null) return;
            var cts = _cloudCts;
            try { cts?.Cancel(); } catch (Exception) { /* già chiuso */ }
            _ = CloudTask.ContinueWith(static (t, state) =>
            {
                if (t.IsCompletedSuccessfully) t.Result?.Dispose();
                ((CancellationTokenSource?)state)?.Dispose();
            }, cts, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            CloudTask = null;
            _cloudCts = null;
        }

        /// <summary>Rinuncia a tutto il pezzo (lettura fermata o pezzo non sintetizzabile).</summary>
        public void Abandon()
        {
            Cached?.Dispose();
            Cached = null;
            AbandonCloud();
            if (LocalTask is { } local)
            {
                _ = local.ContinueWith(static t =>
                {
                    if (t.IsCompletedSuccessfully) t.Result?.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                LocalTask = null;
            }
        }

        /// <summary>L'audio è stato preso in carico dalla riproduzione: si liberano solo le risorse di preparazione.</summary>
        public void Release()
        {
            Cached = null;
            CloudTask = null;
            LocalTask = null;
            _cloudCts?.Dispose();
            _cloudCts = null;
        }
    }

    /// <summary>
    /// Una lettura in corso. Attenzione al rientro: Cancel() esegue i gestori di annullamento sullo stesso thread e questi
    /// possono far proseguire la lettura fino a Finish(); per questo il token viene annullato fuori dal lock e il
    /// CancellationTokenSource viene chiuso solo quando nessun annullamento è in corso.
    /// </summary>
    private sealed class Utterance
    {
        private readonly CancellationTokenSource _cts;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private bool _finished;
        private bool _cancelling;
        private bool _ctsDisposed;
        private volatile bool _superseded;

        public Utterance(CancellationTokenSource cts)
        {
            _cts = cts;
            Token = cts.Token;
        }

        public CancellationToken Token { get; }
        public Task Done => _done.Task;
        public bool Superseded => _superseded;

        public void Cancel(bool superseded)
        {
            lock (_lock)
            {
                if (_finished || _cancelling || _cts.IsCancellationRequested) return;
                _cancelling = true;
                _superseded = superseded;
            }
            try
            {
                _cts.Cancel();
            }
            catch (AggregateException)
            {
                // Errori nei gestori di annullamento di terzi: la lettura si ferma comunque.
            }
            bool dispose;
            lock (_lock)
            {
                _cancelling = false;
                dispose = _finished && !_ctsDisposed;
                if (dispose) _ctsDisposed = true;
            }
            if (dispose) _cts.Dispose();
        }

        public void Finish()
        {
            bool dispose;
            lock (_lock)
            {
                if (_finished) return;
                _finished = true;
                dispose = !_cancelling && !_ctsDisposed;
                if (dispose) _ctsDisposed = true;
            }
            if (dispose) _cts.Dispose();
            _done.TrySetResult();
        }
    }
}
