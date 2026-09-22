using System.Threading.Channels;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Text;

namespace PuntaEAscolta.Logic.Reading;

/// <summary>
/// Cuore dell'app (DESIGN.md 2.1, 2.2, 3.1 parte B): riceve gli eventi di input, riconosce i gesti, applica la regola
/// "se la voce sta parlando qualunque attivazione la ferma e basta", risolve il testo e lo fa pronunciare.
/// <see cref="HandleInput"/> non blocca mai: accoda in un canale letto da un solo worker. Il worker non esegue letture:
/// avvia un compito per richiesta e lo annulla quando ne arriva una nuova, così resta sempre pronto a ricevere lo stop.
/// Non attiva finestre, non tocca il focus, non registra il testo letto (lo fa il risolutore, solo a livello Debug).
/// </summary>
public sealed class ReadOrchestrator : IReadOrchestrator
{
    /// <summary>Messaggio quando si chiede la dettatura ma il servizio non è stato composto.</summary>
    public const string DictationUnavailableText = "Dettatura non disponibile";

    /// <summary>Conferma vocale della scorciatoia di pausa (unico riscontro possibile: l'app è solo audio).</summary>
    public const string PausedText = "Lettura in pausa";
    public const string ResumedText = "Lettura riattivata";

    /// <summary>Lunghezza massima di un pezzo passato al servizio vocale.</summary>
    public const int MaxChunkChars = 400;

    private readonly ITextResolver _resolver;
    private readonly ISpeechService _speech;
    private readonly IDictationService? _dictation;
    private readonly ISettingsStore _settings;
    private readonly ILog _log;
    private readonly Channel<InputEvent> _queue;
    private readonly GestureRecognizer _gestures;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    // Stato usato solo dal worker (e da Dispose dopo l'arresto del worker).
    private CancellationTokenSource? _currentCts;
    private Task _currentTask = Task.CompletedTask;
    private Task _dictationTask = Task.CompletedTask;
    private long _readSeq;
    private long? _lastHotkeyReadMs;

    // Stato condiviso fra thread.
    private long _speakingSeq;
    private volatile bool _paused;
    private int _disposed;

    public ReadOrchestrator(ITextResolver resolver, ISpeechService speech, IDictationService? dictation, ISettingsStore settings, ILog log, TimeProvider? time = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _dictation = dictation;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? NullLog.Instance;
        var timeProvider = time ?? TimeProvider.System;

        try { _paused = settings.Current.General.Paused; }
        catch (Exception ex) { _log.Error("Impossibile leggere lo stato di pausa dalle impostazioni", ex); }

        _queue = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _gestures = new GestureRecognizer(CurrentInputSettings, timeProvider, OnLongPressTimer, OnMouseActivation, _log);
        _worker = Task.Run(RunWorkerAsync);
    }

    public event Action<ReadOutcome>? OutcomeProduced;

    /// <summary>
    /// Sollevato quando <see cref="Paused"/> cambia (anche dalla scorciatoia <see cref="HotkeyAction.TogglePause"/>):
    /// chi compone l'app lo usa per <c>IInputSource.SetPaused</c>, l'icona di notifica e il salvataggio nelle impostazioni.
    /// </summary>
    public event Action<bool>? PausedChanged;

    /// <summary>In pausa i clic del mouse sono ignorati; le scorciatoie da tastiera funzionano ancora.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            _log.Info(value ? "Letture con il mouse in pausa" : "Letture con il mouse riattivate");
            RaisePausedChanged(value);
        }
    }

    public void HandleInput(InputEvent inputEvent)
    {
        if (inputEvent is null || Volatile.Read(ref _disposed) != 0) return;
        _queue.Writer.TryWrite(inputEvent);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { _shutdown.Cancel(); } catch (Exception ex) { _log.Error("Arresto dell'orchestratore: annullamento fallito", ex); }
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { /* il worker registra da sé i propri errori */ }

        CancelCurrent();
        Interlocked.Exchange(ref _speakingSeq, 0);
        try { _gestures.Dispose(); } catch { /* ignorato */ }
        // Voce e dettatura appartengono a chi compone l'app: non si chiudono qui.
    }

    // ---------------------------------------------------------------------------------------------
    // Worker
    // ---------------------------------------------------------------------------------------------

    private async Task RunWorkerAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var ev))
                {
                    if (_shutdown.IsCancellationRequested) return;
                    try { Process(ev); }
                    catch (Exception ex) { _log.Error("Evento di input non elaborato per un errore", ex); }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Arresto normale.
        }
        catch (Exception ex)
        {
            _log.Error("Il worker dell'orchestratore si è fermato per un errore", ex);
        }
    }

    private void Process(InputEvent ev)
    {
        switch (ev)
        {
            case TriggerButtonEvent button:
                if (_paused)
                {
                    _log.Debug("Clic dell'attivatore ignorato: letture in pausa");
                    return;
                }
                _gestures.OnButton(button);
                break;

            case LongPressElapsedEvent elapsed:
                _gestures.OnLongPressElapsed(elapsed.PendingId);
                break;

            case HotkeyEvent hotkey:
                OnHotkey(hotkey);
                break;

            case BarrierEvent barrier:
                barrier.Done.TrySetResult();
                break;

            default:
                _log.Debug($"Evento di input sconosciuto ignorato: {ev.GetType().Name}");
                break;
        }
    }

    private void OnLongPressTimer(long pendingId)
    {
        // Thread del timer: si riaccoda, lo stato del riconoscitore lo tocca solo il worker.
        if (Volatile.Read(ref _disposed) != 0) return;
        _queue.Writer.TryWrite(new LongPressElapsedEvent(pendingId, Environment.TickCount64));
    }

    private void OnMouseActivation(ScreenPoint point, bool isLong)
    {
        if (_paused)
        {
            _log.Debug("Attivazione del mouse ignorata: letture in pausa");
            return;
        }
        Trigger(isLong ? ReadRequestKind.ZoneAroundPointer : ReadRequestKind.AtPointer, point, isLong ? "pressione lunga" : "clic");
    }

    private void OnHotkey(HotkeyEvent hotkey)
    {
        switch (hotkey.Action)
        {
            case HotkeyAction.ReadAtPointer:
            case HotkeyAction.ReadSelection:
                if (IsHotkeyBounce(hotkey)) return;
                var kind = hotkey.Action == HotkeyAction.ReadSelection ? ReadRequestKind.Selection : ReadRequestKind.AtPointer;
                Trigger(kind, hotkey.Point, "scorciatoia");
                break;

            case HotkeyAction.Stop:
                _log.Info("Scorciatoia di stop");
                StopAll();
                break;

            case HotkeyAction.ToggleDictation:
                ToggleDictation();
                break;

            case HotkeyAction.TogglePause:
                TogglePause();
                break;

            default:
                _log.Debug($"Azione di scorciatoia sconosciuta: {hotkey.Action}");
                break;
        }
    }

    /// <summary>Ripetizione automatica del tasto o doppia pressione involontaria: stesso anti-rimbalzo del mouse.</summary>
    private bool IsHotkeyBounce(HotkeyEvent hotkey)
    {
        int debounce = CurrentInputSettings().DebounceMs;
        if (_lastHotkeyReadMs is { } last && hotkey.TimestampMs >= last && hotkey.TimestampMs - last < debounce)
        {
            _log.Debug($"Scorciatoia ignorata dall'anti-rimbalzo ({hotkey.TimestampMs - last} ms dalla precedente)");
            return true;
        }
        _lastHotkeyReadMs = hotkey.TimestampMs;
        return false;
    }

    /// <summary>Attivazione di lettura: se la voce sta parlando la ferma e basta, altrimenti avvia una lettura nuova.</summary>
    private void Trigger(ReadRequestKind kind, ScreenPoint point, string origin)
    {
        if (IsSpeakingNow())
        {
            _log.Info($"Attivazione ({origin}) mentre la voce parla: stop");
            StopAll();
            return;
        }
        _log.Debug($"Attivazione ({origin}): {kind} a {point.X},{point.Y}");
        StartRead(new ReadRequest(kind, point));
    }

    private void ToggleDictation()
    {
        if (_dictation is null)
        {
            _log.Info("Dettatura richiesta ma non disponibile");
            StartSpeech(SystemMessage(DictationUnavailableText));
            return;
        }

        // La lettura in corso non deve finire nel microfono: si ferma la nostra, non l'eventuale rilettura della dettatura.
        bool oursSpeaking = Interlocked.Read(ref _speakingSeq) != 0;
        CancelCurrent();
        if (oursSpeaking)
        {
            Interlocked.Exchange(ref _speakingSeq, 0);
            SafeStopSpeech();
        }

        var dictation = _dictation;
        var token = _shutdown.Token;
        _dictationTask = Task.Run(async () =>
        {
            try
            {
                await dictation.ToggleAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Arresto dell'app.
            }
            catch (Exception ex)
            {
                _log.Error("Dettatura non riuscita", ex);
            }
        });
    }

    private void TogglePause()
    {
        bool paused = !_paused;
        StopAll();
        Paused = paused;
        StartSpeech(SystemMessage(paused ? PausedText : ResumedText));
    }

    // ---------------------------------------------------------------------------------------------
    // Letture
    // ---------------------------------------------------------------------------------------------

    private bool IsSpeakingNow()
    {
        if (Interlocked.Read(ref _speakingSeq) != 0) return true;
        try { return _speech.IsSpeaking; }
        catch (Exception ex)
        {
            _log.Error("Il servizio vocale non risponde a IsSpeaking", ex);
            return false;
        }
    }

    private void StopAll()
    {
        CancelCurrent();
        Interlocked.Exchange(ref _speakingSeq, 0);
        SafeStopSpeech();
    }

    private void SafeStopSpeech()
    {
        try { _speech.Stop(); }
        catch (Exception ex) { _log.Error("Arresto della voce fallito", ex); }
    }

    private void CancelCurrent()
    {
        var cts = _currentCts;
        _currentCts = null;
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (Exception ex) { _log.Error("Annullamento della lettura in corso fallito", ex); }
        // Non si chiama Dispose: il compito può ancora leggere il token; senza timer non ci sono risorse da liberare.
    }

    private void StartRead(ReadRequest request)
    {
        CancelCurrent();
        var cts = new CancellationTokenSource();
        _currentCts = cts;
        long seq = ++_readSeq;
        var token = cts.Token;
        _currentTask = Task.Run(() => RunReadAsync(request, seq, token));
    }

    private void StartSpeech(SpeechRequest request)
    {
        CancelCurrent();
        var cts = new CancellationTokenSource();
        _currentCts = cts;
        long seq = ++_readSeq;
        var token = cts.Token;
        _currentTask = Task.Run(async () =>
        {
            try
            {
                await SpeakCoreAsync(request, seq, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Interrotto da uno stop, da una lettura nuova o dal servizio vocale.
            }
            catch (Exception ex)
            {
                _log.Error("Messaggio vocale non pronunciato", ex);
            }
        });
    }

    private async Task RunReadAsync(ReadRequest request, long seq, CancellationToken ct)
    {
        try
        {
            var outcome = await _resolver.ResolveAsync(request, ct).ConfigureAwait(false)
                ?? ReadOutcome.Nothing(0, "risolutore: esito nullo");
            ct.ThrowIfCancellationRequested();
            RaiseOutcome(outcome);

            var speech = BuildSpeechRequest(outcome, CurrentReadingSettings(), _log);
            if (speech is null) return;
            await SpeakCoreAsync(speech, seq, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Debug($"Lettura {seq} annullata");
        }
        catch (OperationCanceledException)
        {
            // Voce fermata da altri (es. Esc gestito da chi compone l'app): non è un errore.
            _log.Debug($"Lettura {seq} interrotta dal servizio vocale");
        }
        catch (Exception ex)
        {
            _log.Error("Lettura non riuscita", ex);
        }
    }

    private async Task SpeakCoreAsync(SpeechRequest request, long seq, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _speakingSeq, seq);
        try
        {
            await _speech.SpeakAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _speakingSeq, 0, seq);
        }
    }

    /// <summary>
    /// Richiesta per il servizio vocale: il testo trovato spezzato in frasi (tipo, lingua e riservatezza dall'esito),
    /// oppure la frase "nessun testo" come messaggio di sistema, oppure null se non c'è nulla da dire.
    /// </summary>
    internal static SpeechRequest? BuildSpeechRequest(ReadOutcome outcome, ReadingSettings reading, ILog log)
    {
        if (outcome.HasText)
        {
            var text = outcome.Text.Trim();
            IReadOnlyList<string> chunks;
            try
            {
                chunks = SentenceSplitter.SplitSentences(text, MaxChunkChars)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();
            }
            catch (Exception ex)
            {
                log.Error("Spezzatura in frasi fallita: il testo viene letto in un pezzo unico", ex);
                chunks = Array.Empty<string>();
            }
            if (chunks.Count == 0) chunks = new[] { text };
            return new SpeechRequest(text, outcome.SpeechKind, outcome.LanguageHint, outcome.Sensitive) { Chunks = chunks };
        }

        if (reading.SpeakWhenNothingFound && !string.IsNullOrWhiteSpace(reading.NothingFoundText))
            return SystemMessage(reading.NothingFoundText);
        return null;
    }

    /// <summary>Messaggio dell'app: sempre in italiano, con la voce locale (SpeechKind.System).</summary>
    internal static SpeechRequest SystemMessage(string text) =>
        new(text.Trim(), SpeechKind.System, "it", Sensitive: false);

    // ---------------------------------------------------------------------------------------------
    // Supporto
    // ---------------------------------------------------------------------------------------------

    private InputSettings CurrentInputSettings()
    {
        try { return _settings.Current?.Input ?? new InputSettings(); }
        catch (Exception ex)
        {
            _log.Error("Impostazioni di input non leggibili: uso i predefiniti", ex);
            return new InputSettings();
        }
    }

    private ReadingSettings CurrentReadingSettings()
    {
        try { return _settings.Current?.Reading ?? new ReadingSettings(); }
        catch (Exception ex)
        {
            _log.Error("Impostazioni di lettura non leggibili: uso i predefiniti", ex);
            return new ReadingSettings();
        }
    }

    private void RaiseOutcome(ReadOutcome outcome)
    {
        var handlers = OutcomeProduced;
        if (handlers is null) return;
        try { handlers(outcome); }
        catch (Exception ex) { _log.Error("Un gestore di OutcomeProduced ha lanciato un'eccezione", ex); }
    }

    private void RaisePausedChanged(bool paused)
    {
        var handlers = PausedChanged;
        if (handlers is null) return;
        try { handlers(paused); }
        catch (Exception ex) { _log.Error("Un gestore di PausedChanged ha lanciato un'eccezione", ex); }
    }

    // ---------------------------------------------------------------------------------------------
    // Sincronizzazione per i test
    // ---------------------------------------------------------------------------------------------

    /// <summary>Evento interno: il worker lo completa quando ha elaborato tutto ciò che era in coda prima.</summary>
    internal sealed record BarrierEvent(TaskCompletionSource Done) : InputEvent(0);

    /// <summary>Attende che il worker abbia elaborato gli eventi già accodati (non attende le letture avviate).</summary>
    internal Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new BarrierEvent(tcs))) tcs.TrySetResult();
        return tcs.Task;
    }

    /// <summary>Attende la coda e poi la fine della lettura e della dettatura avviate per ultime.</summary>
    internal async Task WaitForIdleAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        await Task.WhenAll(Volatile.Read(ref _currentTask), Volatile.Read(ref _dictationTask)).ConfigureAwait(false);
    }
}
