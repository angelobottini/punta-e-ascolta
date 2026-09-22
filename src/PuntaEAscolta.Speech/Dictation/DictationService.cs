using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Speech.Dictation;

/// <summary>Frasi pronunciate dalla dettatura (voce locale, SpeechKind.System).</summary>
internal static class DictationPhrases
{
    public const string NotAvailable = "Dettatura non disponibile";
    public const string Recording = "Registro";
    public const string Received = "Ricevuto";
    public const string NotUnderstood = "Non ho capito";
    public const string Cancelled = "Annullato";
    public const string Failed = "Dettatura non riuscita";
    public const string MicrophoneUnavailable = "Microfono non disponibile";
    public const string Deleted = "Cancellato";
    public const string NothingToDelete = "Niente da cancellare";
    public const string NewLine = "A capo";
    public const string NothingToReadAgain = "Niente da rileggere";
    public const string InsertFailed = "Inserimento non riuscito";
}

/// <summary>
/// Dettatura ad alternanza: primo richiamo → "Registro" e registrazione; secondo richiamo (o durata massima) → "Ricevuto",
/// trascrizione (al massimo 30 s), comandi vocali a frase intera (cancella, a capo, rileggi), altrimenti rilettura del testo
/// e inserimento. Nessun taglio automatico sulle pause.
/// <list type="bullet">
/// <item>Un richiamo durante la trascrizione o la rilettura annulla ("Annullato", niente inserimento).</item>
/// <item>Una rilettura fermata dall'utente con lo Stop della voce annulla allo stesso modo; se invece viene interrotta da
/// un'altra lettura si annulla in silenzio, per non parlarci sopra.</item>
/// <item>Con <c>AutoInsert</c> spento, dopo la rilettura si attende un nuovo richiamo per inserire (15 s, poi "Annullato").</item>
/// </list>
/// ToggleAsync ritorna appena la transizione è avviata: trascrizione, rilettura e inserimento proseguono in un'attività
/// propria, così un chiamante che annulla il proprio token alla richiesta successiva non interrompe la dettatura.
/// </summary>
public sealed class DictationService : IDictationService
{
    internal static readonly TimeSpan TranscriptionTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan CueWait = TimeSpan.FromSeconds(3);
    internal const int MinMaxSeconds = 1;
    internal const int MaxMaxSeconds = 600;
    private const int PcmBytesPerSecond = 32000;

    private readonly IAudioRecorder _recorder;
    private readonly ISpeechToText _stt;
    private readonly ITextInjector _injector;
    private readonly ISpeechService _speech;
    private readonly ISettingsStore _settings;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly object _eventGate = new();
    private DictationState _state = DictationState.Idle;
    private Session? _session;
    private bool _starting;
    private bool _disposed;
    private Task _pipeline = Task.CompletedTask;
    private string? _lastInserted;
    private string? _lastDictated;

    public DictationService(IAudioRecorder recorder, ISpeechToText stt, ITextInjector injector, ISpeechService speech,
        ISettingsStore settings, ILog log)
        : this(recorder, stt, injector, speech, settings, log, TimeProvider.System)
    {
    }

    /// <summary>Come il costruttore principale, con l'orologio iniettabile (prove).</summary>
    public DictationService(IAudioRecorder recorder, ISpeechToText stt, ITextInjector injector, ISpeechService speech,
        ISettingsStore settings, ILog log, TimeProvider time)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _stt = stt ?? throw new ArgumentNullException(nameof(stt));
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? NullLog.Instance;
        _time = time ?? TimeProvider.System;
    }

    public DictationState State
    {
        get { lock (_sync) return _state; }
    }

    public event Action<DictationState>? StateChanged;

    /// <summary>Ultimo testo inserito (con lo spazio finale), usato da "cancella". Null dopo una cancellazione.</summary>
    public string? LastInsertedText
    {
        get { lock (_sync) return _lastInserted; }
    }

    /// <summary>Pausa dopo la rilettura, prima dell'inserimento automatico: lascia il tempo di annullare.</summary>
    internal TimeSpan ReadBackGrace { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Per le prove: l'attività di trascrizione, rilettura e inserimento in corso o l'ultima.</summary>
    internal Task PipelineTask
    {
        get { lock (_sync) return _pipeline; }
    }

    public async Task ToggleAsync(CancellationToken ct)
    {
        Session? session;
        DictationState state;
        lock (_sync)
        {
            if (_disposed) return;
            if (_starting)
            {
                _log.Debug("Dettatura: avvio già in corso, richiamo ignorato.");
                return;
            }
            state = _state;
            session = _session;
            if (state == DictationState.Idle) _starting = true;
        }

        if (state == DictationState.Idle)
        {
            await StartAsync(ct).ConfigureAwait(false);
            return;
        }
        if (session is null) return;

        if (state == DictationState.Recording)
        {
            StopRecording(session, automatic: false);
        }
        else if (state == DictationState.ReadingBack && session.TryConfirm())
        {
            _log.Info("Dettatura: inserimento confermato.");
        }
        else
        {
            _log.Info($"Dettatura: annullata dall'utente durante {(state == DictationState.Transcribing ? "la trascrizione" : "la rilettura")}.");
            CancelSession(session, announce: true);
        }
    }

    public void Cancel()
    {
        Session? session;
        DictationState state;
        lock (_sync)
        {
            session = _session;
            state = _state;
        }
        if (session is null) return;

        if (state == DictationState.Recording && TryCancelRecording(session)) return;

        lock (_sync)
        {
            session = _session;
            state = _state;
        }
        if (session is null) return;
        if (state == DictationState.Idle)
        {
            session.Cancel(announce: false); // avvio in corso: si ferma in silenzio
            return;
        }
        _log.Info("Dettatura: annullata.");
        CancelSession(session, announce: true);
    }

    /// <summary>Annulla trascrizione o rilettura. La voce si ferma PRIMA, così non taglia l'eventuale "Annullato".</summary>
    private void CancelSession(Session session, bool announce)
    {
        StopSpeechQuietly();
        session.Cancel(announce);
    }

    private bool TryCancelRecording(Session session)
    {
        bool stopped;
        lock (_eventGate)
        {
            lock (_sync)
            {
                stopped = ReferenceEquals(_session, session) && _state == DictationState.Recording;
                if (stopped)
                {
                    _session = null;
                    _state = DictationState.Idle;
                }
            }
            if (stopped) RaiseStateChanged(DictationState.Idle);
        }
        if (!stopped) return false;

        session.Cancel(announce: false);
        session.Dispose();
        StopRecorderDiscarding();
        _log.Info("Dettatura: registrazione annullata.");
        _ = SpeakSystemAsync(DictationPhrases.Cancelled, CancellationToken.None);
        return true;
    }

    public void Dispose()
    {
        Session? session;
        DictationState state;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
            state = _state;
        }
        session?.Cancel(announce: false);
        if (state == DictationState.Recording)
        {
            session?.Dispose();
            StopRecorderDiscarding();
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Avvio e arresto della registrazione
    // -----------------------------------------------------------------------------------------------------------------

    private async Task StartAsync(CancellationToken ct)
    {
        Session? session = null;
        try
        {
            DictationSettings dictation = _settings.Current.Dictation;
            if (!dictation.Enabled || !IsSttConfigured())
            {
                _log.Info($"Dettatura: non disponibile ({(dictation.Enabled ? "chiave ElevenLabs assente o non decifrabile" : "disattivata nelle impostazioni")}).");
                await SpeakSystemAsync(DictationPhrases.NotAvailable, ct).ConfigureAwait(false);
                return;
            }

            session = new Session();
            lock (_sync) _session = session;

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token))
            {
                await SpeakSystemAsync(DictationPhrases.Recording, linked.Token).ConfigureAwait(false);
            }

            if (!TryStartRecorder(dictation.MicrophoneDeviceId))
            {
                ClearSession(session);
                await SpeakSystemAsync(DictationPhrases.MicrophoneUnavailable, ct).ConfigureAwait(false);
                return;
            }

            bool started;
            lock (_eventGate)
            {
                lock (_sync)
                {
                    started = !_disposed && !session.IsCancelled && ReferenceEquals(_session, session);
                    if (started)
                    {
                        _state = DictationState.Recording;
                        int seconds = Math.Clamp(dictation.MaxSeconds, MinMaxSeconds, MaxMaxSeconds);
                        session.MaxTimer = _time.CreateTimer(static state =>
                        {
                            var (service, owner) = ((DictationService, Session))state!;
                            service.OnMaxDuration(owner);
                        }, (this, session), TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
                    }
                }
                if (started) RaiseStateChanged(DictationState.Recording);
            }

            if (!started)
            {
                StopRecorderDiscarding();
                ClearSession(session);
                return;
            }
            _log.Info("Dettatura: registrazione avviata.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Annullata durante il segnale di avvio.
            if (session is not null) ClearSession(session);
        }
        catch (OperationCanceledException)
        {
            if (session is not null) ClearSession(session);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Dettatura: avvio non riuscito.", ex);
            if (session is not null) ClearSession(session);
        }
        finally
        {
            lock (_sync) _starting = false;
        }
    }

    private void OnMaxDuration(Session session)
    {
        try
        {
            StopRecording(session, automatic: true);
        }
        catch (Exception ex)
        {
            _log.Error("Dettatura: arresto automatico non riuscito.", ex);
        }
    }

    private void StopRecording(Session session, bool automatic)
    {
        lock (_eventGate)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_session, session) || _state != DictationState.Recording) return;
                _state = DictationState.Transcribing;
                session.MaxTimer?.Dispose();
                session.MaxTimer = null;
            }
            RaiseStateChanged(DictationState.Transcribing);
        }

        byte[]? pcm;
        try
        {
            pcm = _recorder.Stop();
        }
        catch (Exception ex)
        {
            _log.Error("Dettatura: arresto della registrazione non riuscito.", ex);
            pcm = null;
        }

        _log.Info(automatic
            ? $"Dettatura: durata massima raggiunta, {Seconds(pcm):F1} s registrati."
            : $"Dettatura: {Seconds(pcm):F1} s registrati, trascrizione in corso.");

        Task pipeline = Task.Run(() => ProcessAsync(session, pcm));
        lock (_sync) _pipeline = pipeline;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Trascrizione, comandi, rilettura e inserimento
    // -----------------------------------------------------------------------------------------------------------------

    private async Task ProcessAsync(Session session, byte[]? pcm)
    {
        CancellationToken token = session.Token;
        try
        {
            if (pcm is null)
            {
                await SpeakSystemAsync(DictationPhrases.Failed, token).ConfigureAwait(false);
                return;
            }

            DictationSettings dictation = _settings.Current.Dictation;
            string language = string.IsNullOrWhiteSpace(dictation.Language) ? "it" : dictation.Language.Trim();

            // Segnale di fine registrazione mentre la trascrizione è in corso.
            Task cue = SpeakSystemAsync(DictationPhrases.Received, token);

            string text;
            try
            {
                text = await TranscribeAsync(pcm, language, token).ConfigureAwait(false);
            }
            catch (SpeechToTextException ex)
            {
                _log.Warn($"Dettatura: trascrizione non riuscita ({ex.Reason}, HTTP {ex.StatusCode}).");
                await WaitForCueAsync(cue, token).ConfigureAwait(false);
                await SpeakSystemAsync(ex.Reason == SpeechToTextFailure.NotConfigured ? DictationPhrases.NotAvailable : DictationPhrases.Failed, token)
                    .ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                _log.Warn($"Dettatura: nessuna trascrizione entro {TranscriptionTimeout.TotalSeconds:F0} s.");
                await WaitForCueAsync(cue, token).ConfigureAwait(false);
                await SpeakSystemAsync(DictationPhrases.Failed, token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error("Dettatura: errore imprevisto nella trascrizione.", ex);
                await WaitForCueAsync(cue, token).ConfigureAwait(false);
                await SpeakSystemAsync(DictationPhrases.Failed, token).ConfigureAwait(false);
                return;
            }

            await WaitForCueAsync(cue, token).ConfigureAwait(false);
            text = (text ?? "").Trim();
            if (_log.IsDebugEnabled) _log.Debug($"Dettatura: riconosciuto \"{text}\".");

            if (text.Length == 0)
            {
                _log.Info("Dettatura: nessun testo riconosciuto.");
                await SpeakSystemAsync(DictationPhrases.NotUnderstood, token).ConfigureAwait(false);
                return;
            }

            VoiceCommand command = VoiceCommands.Match(text, dictation);
            if (command != VoiceCommand.None)
            {
                _log.Info($"Dettatura: comando vocale {command}.");
                await ExecuteCommandAsync(command, language, token).ConfigureAwait(false);
                return;
            }

            if (dictation.ReadBack)
            {
                SetState(session, DictationState.ReadingBack);
                SpeechOutcome outcome = await SpeakTextAsync(text, language, token).ConfigureAwait(false);
                if (outcome == SpeechOutcome.Stopped)
                {
                    _log.Info("Dettatura: rilettura fermata, testo non inserito.");
                    session.Cancel(announce: true);
                    token.ThrowIfCancellationRequested();
                }
                else if (outcome == SpeechOutcome.Superseded)
                {
                    _log.Info("Dettatura: rilettura interrotta da un'altra lettura, testo non inserito.");
                    session.Cancel(announce: false);
                    token.ThrowIfCancellationRequested();
                }
                if (dictation.AutoInsert && ReadBackGrace > TimeSpan.Zero)
                {
                    await Task.Delay(ReadBackGrace, _time, token).ConfigureAwait(false);
                }
            }

            if (!dictation.AutoInsert)
            {
                Task<bool> confirmed = session.ArmConfirmation();
                Task expired = Task.Delay(ConfirmTimeout, _time, token);
                SetState(session, DictationState.ReadingBack);
                _log.Info("Dettatura: in attesa di conferma (nuovo richiamo per inserire).");
                Task finished = await Task.WhenAny(confirmed, expired).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(finished, confirmed))
                {
                    _log.Info("Dettatura: nessuna conferma, testo non inserito.");
                    await SpeakSystemAsync(DictationPhrases.Cancelled, token).ConfigureAwait(false);
                    return;
                }
            }

            await InsertAsync(text, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (session.Announce && !IsDisposed)
            {
                await SpeakSystemAsync(DictationPhrases.Cancelled, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Dettatura: errore imprevisto.", ex);
        }
        finally
        {
            ClearSession(session);
        }
    }

    private async Task<string> TranscribeAsync(byte[] pcm, string language, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(TranscriptionTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            return await _stt.TranscribeAsync(pcm, language, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException("Trascrizione oltre il tempo massimo.");
        }
    }

    private async Task ExecuteCommandAsync(VoiceCommand command, string language, CancellationToken token)
    {
        switch (command)
        {
            case VoiceCommand.Delete:
            {
                string? last;
                lock (_sync) last = _lastInserted;
                if (string.IsNullOrEmpty(last))
                {
                    await SpeakSystemAsync(DictationPhrases.NothingToDelete, token).ConfigureAwait(false);
                    return;
                }
                if (!await TryInjectAsync(() => _injector.PressBackspaceAsync(last.Length, token)).ConfigureAwait(false)) return;
                lock (_sync)
                {
                    _lastInserted = null;
                    _lastDictated = null;
                }
                await SpeakSystemAsync(DictationPhrases.Deleted, token).ConfigureAwait(false);
                return;
            }

            case VoiceCommand.NewLine:
                if (!await TryInjectAsync(() => _injector.PressEnterAsync(token)).ConfigureAwait(false)) return;
                lock (_sync) _lastInserted = "\n"; // "cancella" subito dopo toglie solo l'a capo
                await SpeakSystemAsync(DictationPhrases.NewLine, token).ConfigureAwait(false);
                return;

            case VoiceCommand.ReadAgain:
            {
                string? text;
                lock (_sync) text = _lastDictated;
                if (string.IsNullOrEmpty(text))
                {
                    await SpeakSystemAsync(DictationPhrases.NothingToReadAgain, token).ConfigureAwait(false);
                    return;
                }
                await SpeakTextAsync(text, language, token).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task InsertAsync(string text, CancellationToken token)
    {
        string typed = text + " ";
        if (!await TryInjectAsync(() => _injector.TypeTextAsync(typed, token)).ConfigureAwait(false)) return;
        lock (_sync)
        {
            _lastInserted = typed;
            _lastDictated = text;
        }
        _log.Info($"Dettatura: inseriti {typed.Length} caratteri.");
    }

    /// <summary>Esegue un'azione dell'iniettore; se fallisce registra, avvisa a voce e restituisce falso.</summary>
    private async Task<bool> TryInjectAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Dettatura: inserimento del testo non riuscito.", ex);
            await SpeakSystemAsync(DictationPhrases.InsertFailed, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Voce
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>Messaggio breve con la voce locale. Mai eccezioni, salvo l'annullamento di <paramref name="ct"/>.</summary>
    private async Task SpeakSystemAsync(string text, CancellationToken ct)
    {
        try
        {
            await _speech.SpeakAsync(new SpeechRequest(text, SpeechKind.System, "it"), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"Dettatura: messaggio vocale non riuscito ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>Rilettura del testo dettato: è testo dell'utente, quindi Sensitive (rispetta l'opzione di riservatezza).</summary>
    private async Task<SpeechOutcome> SpeakTextAsync(string text, string language, CancellationToken ct)
    {
        var request = new SpeechRequest(text, SpeechKind.Sentence, LanguageHintOf(language), Sensitive: true);
        try
        {
            if (_speech is ISpeechServiceWithOutcome withOutcome)
            {
                return await withOutcome.SpeakWithOutcomeAsync(request, ct).ConfigureAwait(false);
            }
            await _speech.SpeakAsync(request, ct).ConfigureAwait(false);
            return SpeechOutcome.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"Dettatura: rilettura non riuscita ({ex.GetType().Name}: {ex.Message}).");
            return SpeechOutcome.Failed;
        }
    }

    private async Task WaitForCueAsync(Task cue, CancellationToken token)
    {
        if (!cue.IsCompleted)
        {
            await Task.WhenAny(cue, Task.Delay(CueWait, _time, token)).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
    }

    private void StopSpeechQuietly()
    {
        try
        {
            _speech.Stop();
        }
        catch (Exception ex)
        {
            _log.Warn($"Dettatura: arresto della voce non riuscito ({ex.GetType().Name}).");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Utilità
    // -----------------------------------------------------------------------------------------------------------------

    private bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

    private bool IsSttConfigured()
    {
        try
        {
            return _stt.IsConfigured;
        }
        catch (Exception ex)
        {
            _log.Warn($"Dettatura: stato del servizio di trascrizione non leggibile ({ex.GetType().Name}).");
            return false;
        }
    }

    private bool TryStartRecorder(string? configuredDevice)
    {
        string? device = string.IsNullOrWhiteSpace(configuredDevice) ? null : configuredDevice.Trim();
        try
        {
            _recorder.Start(device);
            return true;
        }
        catch (Exception ex) when (device is not null)
        {
            _log.Warn($"Dettatura: microfono configurato non disponibile ({ex.GetType().Name}: {ex.Message}), provo quello predefinito.");
        }
        catch (Exception ex)
        {
            _log.Error("Dettatura: microfono non disponibile.", ex);
            return false;
        }

        try
        {
            _recorder.Start(null);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Dettatura: microfono non disponibile.", ex);
            return false;
        }
    }

    private void StopRecorderDiscarding()
    {
        try
        {
            if (_recorder.IsRecording) _recorder.Stop();
        }
        catch (Exception ex)
        {
            _log.Warn($"Dettatura: arresto della registrazione non riuscito ({ex.GetType().Name}).");
        }
    }

    private void SetState(Session session, DictationState state)
    {
        lock (_eventGate)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_session, session) || _state == state) return;
                _state = state;
            }
            RaiseStateChanged(state);
        }
    }

    /// <summary>Fine della sessione: torna a Idle (se la sessione è ancora quella corrente) e rilascia le risorse.</summary>
    private void ClearSession(Session session)
    {
        bool changed = false;
        lock (_eventGate)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                    changed = _state != DictationState.Idle;
                    _state = DictationState.Idle;
                }
            }
            if (changed) RaiseStateChanged(DictationState.Idle);
        }
        session.Dispose();
    }

    /// <summary>Da chiamare dentro _eventGate: gli eventi escono nell'ordine delle transizioni.</summary>
    private void RaiseStateChanged(DictationState state)
    {
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            _log.Error("Dettatura: errore in un gestore di StateChanged.", ex);
        }
    }

    private static string? LanguageHintOf(string language)
    {
        string code = language.Trim();
        int separator = code.IndexOfAny(['-', '_']);
        if (separator > 0) code = code[..separator];
        code = code.ToLowerInvariant();
        return code switch
        {
            "it" or "ita" => "it",
            "en" or "eng" => "en",
            _ => null
        };
    }

    private static double Seconds(byte[]? pcm) => pcm is null ? 0 : pcm.Length / (double)PcmBytesPerSecond;

    /// <summary>
    /// Una dettatura dall'avvio alla fine. Il token si annulla con Cancel(); come per la voce, l'annullamento avviene fuori
    /// dal lock e il CancellationTokenSource si chiude solo quando nessun annullamento è in corso (i gestori girano sullo stesso thread).
    /// </summary>
    private sealed class Session : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();
        private bool _cancelling;
        private bool _disposeRequested;
        private bool _ctsDisposed;
        private TaskCompletionSource<bool>? _confirmation;

        public Session()
        {
            Token = _cts.Token;
        }

        public CancellationToken Token { get; }
        public ITimer? MaxTimer { get; set; }
        public bool IsCancelled => Token.IsCancellationRequested;

        /// <summary>Se falso l'annullamento è silenzioso (chiusura del servizio, rilettura sostituita da un'altra lettura).</summary>
        public bool Announce { get; private set; } = true;

        public Task<bool> ArmConfirmation()
        {
            lock (_lock)
            {
                _confirmation ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _confirmation.Task;
            }
        }

        /// <summary>Vero se c'era una conferma in attesa e ora è data.</summary>
        public bool TryConfirm()
        {
            lock (_lock)
            {
                return _confirmation is not null && !Token.IsCancellationRequested && _confirmation.TrySetResult(true);
            }
        }

        public void Cancel(bool announce)
        {
            lock (_lock)
            {
                if (_ctsDisposed || _cancelling || Token.IsCancellationRequested) return;
                _cancelling = true;
                Announce = announce;
            }
            try
            {
                _cts.Cancel();
            }
            catch (AggregateException)
            {
                // Errori nei gestori di annullamento: la sessione si chiude comunque.
            }
            bool dispose;
            lock (_lock)
            {
                _cancelling = false;
                dispose = _disposeRequested && !_ctsDisposed;
                if (dispose) _ctsDisposed = true;
            }
            if (dispose) DisposeCore();
        }

        public void Dispose()
        {
            bool dispose;
            lock (_lock)
            {
                _disposeRequested = true;
                dispose = !_cancelling && !_ctsDisposed;
                if (dispose) _ctsDisposed = true;
            }
            if (dispose) DisposeCore();
        }

        private void DisposeCore()
        {
            try { MaxTimer?.Dispose(); } catch (Exception) { /* già chiuso */ }
            MaxTimer = null;
            _cts.Dispose();
        }
    }
}
