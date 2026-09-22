using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PuntaEAscolta.App.Hosting;
using PuntaEAscolta.App.SettingsUi;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Reading;
using PuntaEAscolta.Logic.Settings;
using PuntaEAscolta.Windows.Input;
using Forms = System.Windows.Forms;

namespace PuntaEAscolta.App.Tray;

/// <summary>
/// Stato dell'app in modalità icona di notifica: orchestratore, sorgente di input, collegamenti fra i servizi,
/// menu dell'icona e finestra impostazioni. Tutto ciò che tocca l'interfaccia passa dal Dispatcher WPF.
/// </summary>
internal sealed class TrayController : ISettingsHost, IDisposable
{
    private const string AppTitle = "Punta e Ascolta";

    private readonly Application _app;
    private readonly SingleInstance _instance;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _outcomeGate = new();
    private readonly object _settingsGate = new();
    private readonly object _pauseSaveGate = new();
    private readonly object _stopKeyGate = new();

    /// <summary>Vero sul thread che sta salvando lo stato di pausa deciso dall'orchestratore (Changed arriva sincrono lì).</summary>
    [ThreadStatic] private static bool t_persistingPause;
    private int _pausePersistQueued;

    private ReadOrchestrator? _orchestrator;
    private WindowsInputSource? _input;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _menu;
    private Forms.ToolStripMenuItem? _pauseItem;
    private TrayIcons? _icons;
    private ForegroundTracker? _foreground;
    private SettingsWindow? _settingsWindow;
    private string _appliedInputJson = "";
    private int _lastCacheMaxMegabytes;
    private ReadOutcome? _lastOutcome;
    private DateTime _lastOutcomeAt;
    private int _disposed;

    public TrayController(AppServices services, Application app, SingleInstance instance)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
    }

    public AppServices Services { get; }

    private ILog Log => Services.Log;

    private JsonSettingsStore Store => Services.SettingsStore;

    public ReadOrchestrator Orchestrator => _orchestrator ?? throw new InvalidOperationException("App non avviata");

    public IInputSource Input => _input ?? throw new InvalidOperationException("App non avviata");

    public IReadOnlyList<string> InputProblems => _input?.LastProblems ?? Array.Empty<string>();

    public bool Paused => _orchestrator?.Paused ?? false;

    /// <summary>Ultimo esito di lettura (per la scheda Diagnostica) e l'ora in cui è stato prodotto.</summary>
    public (ReadOutcome? Outcome, DateTime At) LastOutcome
    {
        get { lock (_outcomeGate) return (_lastOutcome, _lastOutcomeAt); }
    }

    public Dispatcher Dispatcher => _app.Dispatcher;

    // ---------------------------------------------------------------------------------------------
    // Avvio
    // ---------------------------------------------------------------------------------------------

    public void Start()
    {
        var current = Store.Current;
        _lastCacheMaxMegabytes = current.Speech.CacheMaxMegabytes;

        _orchestrator = new ReadOrchestrator(Services.Resolver, Services.Speech, Services.Dictation, Store, Log);
        _input = new WindowsInputSource(Log);

        // Collegamenti (tutti non bloccanti o quasi: i gestori girano su thread di lavoro).
        _input.Input += _orchestrator.HandleInput;
        _orchestrator.PausedChanged += OnPausedChanged;
        _orchestrator.OutcomeProduced += OnOutcomeProduced;
        _orchestrator.BusyChanged += OnBusyChanged;
        Services.Speech.SpeakingChanged += OnSpeakingChanged;
        Services.Dictation.StateChanged += OnDictationStateChanged;
        Store.Changed += OnSettingsChanged;

        _input.Start(current.Input, current.Dictation);
        _appliedInputJson = InputFingerprint(current);
        _input.SetPaused(_orchestrator.Paused);
        var problems = _input.LastProblems.ToArray();

        CreateTrayIcon();
        _foreground = new ForegroundTracker();
        _instance.Listen(() => BeginOnUi(ShowSettings));
        _instance.ListenForExit(() =>
        {
            Log.Info("Chiusura richiesta dalla riga di comando (--exit)");
            BeginOnUi(() => _app.Shutdown());
        });

        if (current.General.StartWithWindows) StartupRegistration.Apply(true, Log);

        StartBackgroundWork(problems, current);
        Log.Info($"Pronto: attivatore {current.Input.MouseTrigger}, fornitore voce {current.Speech.Provider}, OCR {current.Ocr.Mode}" +
                 (_orchestrator.Paused ? ", in pausa" : ""));
    }

    private void StartBackgroundWork(IReadOnlyList<string> problems, AppSettings current)
    {
        var token = _shutdown.Token;

        // Audio: motore e voce locale pronti prima della prima lettura. Si parla all'avvio SOLO se ci sono problemi.
        _ = Task.Run(async () =>
        {
            try
            {
                var warm = Task.WhenAll(Services.Player.WarmUpAsync(token), Services.WindowsVoice.WarmUpAsync(token));
                if (problems.Count > 0)
                {
                    Log.Warn("Problemi all'avvio: " + string.Join(" | ", problems));
                    await Task.WhenAny(warm, Task.Delay(3000, token)).ConfigureAwait(false);
                    string text = "Punta e Ascolta. Attenzione. " + string.Join(". ", problems.Select(ForSpeech));
                    await Services.Speech.SpeakAsync(new SpeechRequest(text, SpeechKind.System, "it"), token).ConfigureAwait(false);
                }
                await warm.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // chiusura durante l'avvio
            }
            catch (Exception ex)
            {
                Log.Error("Preparazione dell'audio non riuscita", ex);
            }
        }, CancellationToken.None);

        // OCR ONNX: caricato dopo qualche secondo sul suo thread a priorità bassa (non serve se si usa solo Windows OCR).
        if (current.Ocr.Mode != OcrMode.WindowsOnly)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                    var stopwatch = Stopwatch.StartNew();
                    await Services.OnnxOcr.WarmUpAsync().ConfigureAwait(false);
                    if (Services.OnnxOcr.IsAvailable) Log.Info($"Riscaldamento dell'OCR ONNX in sottofondo terminato ({stopwatch.ElapsedMilliseconds} ms)");
                    else Log.Warn($"OCR ONNX non disponibile: {Services.OnnxOcr.UnavailableReason}");
                }
                catch (OperationCanceledException)
                {
                    // chiusura
                }
                catch (Exception ex)
                {
                    Log.Error("Riscaldamento dell'OCR ONNX non riuscito", ex);
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>"Win+Shift+A" si legge meglio come "Win più Shift più A".</summary>
    private static string ForSpeech(string problem) => problem.Replace("+", " più ", StringComparison.Ordinal).Trim().TrimEnd('.');

    private static string InputFingerprint(AppSettings s) => JsonSerializer.Serialize(new { s.Input, s.Dictation });

    // ---------------------------------------------------------------------------------------------
    // Gestori degli eventi dei servizi (thread di lavoro)
    // ---------------------------------------------------------------------------------------------

    private void OnSpeakingChanged(bool speaking) => UpdateStopKey(null);

    /// <summary>La lettura ha cominciato o finito di cercare il testo (o di parlare): Esc deve seguire.</summary>
    private void OnBusyChanged(bool busy) => UpdateStopKey(null);

    /// <summary>
    /// Esc ferma la voce e annulla la ricerca del testo: si registra mentre la voce parla (anche la rilettura della dettatura)
    /// e mentre l'orchestratore è occupato con una lettura, compresa la fase silenziosa di ricerca. Voce e orchestratore
    /// notificano da thread diversi e senza ordine garantito: sotto il lock si rileggono gli stati attuali, così l'ultima
    /// chiamata usa sempre quelli più recenti e Esc non resta sottratto al sistema a lettura finita.
    /// </summary>
    private void UpdateStopKey(AppSettings? settings)
    {
        try
        {
            lock (_stopKeyGate)
            {
                bool busy = Services.Speech.IsSpeaking || (_orchestrator?.IsBusy ?? false);
                bool enabled = (settings ?? Store.Current).Input.EscStopsSpeech;
                _input?.SetStopKeyActive(busy && enabled);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Aggiornamento del tasto Esc non riuscito", ex);
        }
    }

    private void OnPausedChanged(bool paused)
    {
        try
        {
            _input?.SetPaused(paused);
        }
        catch (Exception ex)
        {
            Log.Error("Pausa dell'intercettazione non riuscita", ex);
        }

        BeginOnUi(UpdateTrayState);

        // Il salvataggio non deve bloccare il worker dell'orchestratore. Si salva lo stato ATTUALE dell'orchestratore, non
        // quello catturato qui: con due pressioni ravvicinate un salvataggio in ritardo del valore vecchio rimetteva
        // l'app in pausa in silenzio (e nel file) proprio dopo che la voce aveva detto "Lettura riattivata".
        if (Interlocked.Exchange(ref _pausePersistQueued, 1) == 0) _ = Task.Run(PersistPause);
    }

    private void PersistPause()
    {
        Interlocked.Exchange(ref _pausePersistQueued, 0);
        try
        {
            lock (_pauseSaveGate)
            {
                if (_orchestrator is not { } orchestrator) return;
                bool paused = orchestrator.Paused;
                if (Store.Current.General.Paused == paused) return;
                t_persistingPause = true;
                try
                {
                    // Si cambia SOLO la pausa sul file riletto dal disco: una modifica fatta da fuori (a mano, --set-key ad
                    // app chiusa e poi riaperta male...) non viene riscritta con i valori vecchi in memoria.
                    // Changed arriva su questo thread: ApplySettings sa che non deve toccare la pausa.
                    Store.Update(s => s.General.Paused = paused);
                }
                finally
                {
                    t_persistingPause = false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Salvataggio dello stato di pausa non riuscito", ex);
        }
    }

    private void OnOutcomeProduced(ReadOutcome outcome)
    {
        lock (_outcomeGate)
        {
            _lastOutcome = outcome;
            _lastOutcomeAt = DateTime.Now;
        }
    }

    private void OnDictationStateChanged(DictationState state) => BeginOnUi(UpdateTrayState);

    private void OnSettingsChanged(AppSettings settings)
    {
        // Salvataggi dalla finestra (thread UI) e della pausa (thread del pool) possono sovrapporsi.
        lock (_settingsGate) ApplySettings(settings);
        BeginOnUi(UpdateTrayState);
    }

    private void ApplySettings(AppSettings settings)
    {
        try
        {
            string fingerprint = InputFingerprint(settings);
            if (!string.Equals(fingerprint, _appliedInputJson, StringComparison.Ordinal))
            {
                _appliedInputJson = fingerprint;
                _input?.Apply(settings.Input, settings.Dictation);
                var problems = _input?.LastProblems ?? Array.Empty<string>();
                if (problems.Count > 0) Log.Warn("Problemi di attivazione dopo il salvataggio: " + string.Join(" | ", problems));
                else Log.Info("Attivazione aggiornata");
            }

            // Solo i salvataggi della finestra impostazioni decidono la pausa; quelli che la registrano soltanto no.
            if (!t_persistingPause && _orchestrator is { } orchestrator && orchestrator.Paused != settings.General.Paused)
                orchestrator.Paused = settings.General.Paused;

            UpdateStopKey(settings);

            int max = settings.Speech.CacheMaxMegabytes;
            if (max < _lastCacheMaxMegabytes)
            {
                _ = Task.Run(() =>
                {
                    try { Services.Cache.Trim(); }
                    catch (Exception ex) { Log.Error("Riduzione della cache non riuscita", ex); }
                });
            }
            _lastCacheMaxMegabytes = max;
        }
        catch (Exception ex)
        {
            Log.Error("Applicazione delle nuove impostazioni non riuscita", ex);
        }
    }

    private void BeginOnUi(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                try { action(); }
                catch (Exception ex) { Log.Error("Operazione sull'interfaccia non riuscita", ex); }
            });
        }
        catch (Exception)
        {
            // Dispatcher già chiuso.
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Icona di notifica
    // ---------------------------------------------------------------------------------------------

    private void CreateTrayIcon()
    {
        _icons = new TrayIcons();
        _menu = new Forms.ContextMenuStrip();

        _pauseItem = new Forms.ToolStripMenuItem("Pausa", null, (_, _) => Safe("Pausa", TogglePause));
        var settingsItem = new Forms.ToolStripMenuItem("Impostazioni...", null, (_, _) => Safe("Impostazioni", ShowSettings));
        settingsItem.Font = new System.Drawing.Font(settingsItem.Font, System.Drawing.FontStyle.Bold);
        var selectionItem = new Forms.ToolStripMenuItem("Leggi la selezione", null, (_, _) => Safe("Leggi la selezione", ReadSelectionFromMenu));
        var logsItem = new Forms.ToolStripMenuItem("Apri cartella dei log", null, (_, _) => Safe("Apri cartella dei log", OpenLogFolder));
        var exitItem = new Forms.ToolStripMenuItem("Esci", null, (_, _) => Safe("Esci", Exit));

        _menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _pauseItem, settingsItem, selectionItem, logsItem, new Forms.ToolStripSeparator(), exitItem,
        });

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icons.Normal,
            Text = AppTitle,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => Safe("Impostazioni", ShowSettings);
        UpdateTrayState();
    }

    private void UpdateTrayState()
    {
        if (_notifyIcon is null || _pauseItem is null || _icons is null || _orchestrator is null) return;
        bool paused = _orchestrator.Paused;
        _pauseItem.Text = paused ? "Riprendi" : "Pausa";
        _notifyIcon.Icon = paused ? _icons.Paused : _icons.Normal;

        string text = AppTitle + (paused ? ": in pausa" : "");
        text += Services.Dictation.State switch
        {
            DictationState.Recording => " (dettatura: registro)",
            DictationState.Transcribing => " (dettatura: trascrivo)",
            DictationState.ReadingBack => " (dettatura: rileggo)",
            _ => "",
        };
        // NotifyIcon.Text ammette al massimo 127 caratteri.
        _notifyIcon.Text = text.Length > 127 ? text[..127] : text;

        _settingsWindow?.OnPausedChangedExternally(paused);
    }

    /// <summary>
    /// Pausa dal menu dell'icona: passa dal worker dell'orchestratore come la scorciatoia, così un solo thread cambia lo
    /// stato (niente letture e scritture incrociate) e la voce conferma "Lettura in pausa" o "Lettura riattivata".
    /// </summary>
    private void TogglePause()
    {
        _orchestrator?.HandleInput(new HotkeyEvent(HotkeyAction.TogglePause, NativePointer.GetPhysicalPosition(), Environment.TickCount64));
    }

    public void ShowSettings()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_settingsWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new SettingsWindow(this);
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// "Leggi la selezione" dal menu: il clic sull'icona ha attivato la barra delle applicazioni, quindi si riporta in
    /// primo piano la finestra dove era il testo selezionato e poi si chiede la lettura come con la scorciatoia.
    /// </summary>
    private async void ReadSelectionFromMenu()
    {
        try
        {
            bool restored = _foreground?.RestoreLast() ?? false;
            await Task.Delay(restored ? 300 : 150, _shutdown.Token);
            _orchestrator?.HandleInput(new HotkeyEvent(HotkeyAction.ReadSelection, NativePointer.GetPhysicalPosition(), Environment.TickCount64));
        }
        catch (OperationCanceledException)
        {
            // chiusura
        }
        catch (Exception ex)
        {
            Log.Error("Lettura della selezione dal menu non riuscita", ex);
        }
    }

    public void OpenLogFolder() => OpenFolder(Services.FileLog.DirectoryPath);

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Impossibile aprire la cartella {path}", ex);
        }
    }

    private void Exit()
    {
        Log.Info("Uscita richiesta dal menu");
        _app.Shutdown();
    }

    private void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"Menu \"{what}\" non riuscito", ex);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Chiusura
    // ---------------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { /* ignorato */ }

        // Prima l'input (niente più eventi), poi l'orchestratore; voce e dettatura le chiude la composizione.
        if (_input is not null)
        {
            if (_orchestrator is not null) _input.Input -= _orchestrator.HandleInput;
            TryDispose("input", _input);
        }
        if (_orchestrator is not null)
        {
            _orchestrator.PausedChanged -= OnPausedChanged;
            _orchestrator.OutcomeProduced -= OnOutcomeProduced;
            _orchestrator.BusyChanged -= OnBusyChanged;
            TryDispose("orchestratore", _orchestrator);
        }
        Services.Speech.SpeakingChanged -= OnSpeakingChanged;
        Services.Dictation.StateChanged -= OnDictationStateChanged;
        Store.Changed -= OnSettingsChanged;

        if (_foreground is not null) TryDispose("finestra in primo piano", _foreground);
        if (_notifyIcon is not null)
        {
            try { _notifyIcon.Visible = false; } catch (Exception) { /* ignorato */ }
            TryDispose("icona di notifica", _notifyIcon);
        }
        if (_menu is not null) TryDispose("menu", _menu);
        if (_icons is not null) TryDispose("icone", _icons);
        _shutdown.Dispose();
    }

    private void TryDispose(string what, IDisposable item)
    {
        try
        {
            item.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Chiusura di {what} non riuscita", ex);
        }
    }
}
