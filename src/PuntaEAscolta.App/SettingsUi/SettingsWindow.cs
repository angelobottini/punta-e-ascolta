using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PuntaEAscolta.App.Hosting;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Settings;
using PuntaEAscolta.Windows.Input;

namespace PuntaEAscolta.App.SettingsUi;

/// <summary>
/// Finestra impostazioni per l'assistente (mai mostrata da sola). Costruita in codice, senza associazioni: ogni campo
/// registra un "raccoglitore" che, al salvataggio, scrive il proprio valore in una copia delle impostazioni correnti o
/// restituisce un messaggio d'errore. "Salva" applica subito tutto (l'app ascolta ISettingsStore.Changed).
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private const double LabelWidth = 250;
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private readonly ISettingsHost _host;
    private readonly AppServices _services;
    private readonly JsonSettingsStore _store;
    private readonly ILog _log;
    private readonly List<Func<AppSettings, string?>> _collectors = new();
    private readonly TextBlock _status;
    private readonly TabControl _tabs;
    private CheckBox? _pausedCheck;
    private TextBlock? _problemsText;
    private CancellationTokenSource? _operationCts;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        _services = host.Services;
        _store = _services.SettingsStore;
        _log = _services.Log;

        Title = "Punta e Ascolta - Impostazioni";
        Width = 780;
        Height = 720;
        MinWidth = 600;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13;
        TrySetIcon();

        var initial = JsonSettingsStore.Clone(_store.Current);

        _tabs = new TabControl { Margin = new Thickness(8, 8, 8, 0) };
        _tabs.Items.Add(Tab("Generale", BuildGeneralTab(initial)));
        _tabs.Items.Add(Tab("Attivazione", BuildActivationTab(initial)));
        _tabs.Items.Add(Tab("Lettura", BuildReadingTab(initial)));
        _tabs.Items.Add(Tab("Voce", BuildVoiceTab(initial)));
        _tabs.Items.Add(Tab("Dettatura", BuildDictationTab(initial)));
        var diagnosticsTab = Tab("Diagnostica", BuildDiagnosticsTab());
        _tabs.Items.Add(diagnosticsTab);
        _tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == _tabs && _tabs.SelectedItem == diagnosticsTab) RefreshDiagnostics();
        };

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var save = new Button { Content = "Salva", IsDefault = true, MinWidth = 100, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Chiudi", IsCancel = true, MinWidth = 100, Padding = new Thickness(10, 4, 10, 4) };
        save.Click += (_, _) => Save();
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save);
        buttons.Children.Add(close);
        var bottom = new DockPanel { Margin = new Thickness(12, 8, 12, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_tabs);
        Content = root;

        Loaded += (_, _) => StartInitialLoads();
        Closed += (_, _) =>
        {
            try { _operationCts?.Cancel(); } catch (ObjectDisposedException) { /* ignorato */ }
        };
    }

    private void TrySetIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(path)) Icon = BitmapFrame.Create(new Uri(path, UriKind.Absolute));
        }
        catch (Exception ex)
        {
            _log.Debug($"Icona della finestra non caricata: {ex.Message}");
        }
    }

    /// <summary>Numero di schede (anteprima a riga di comando).</summary>
    internal int PageCount => _tabs.Items.Count;

    /// <summary>Titolo e contenuto di una scheda (anteprima a riga di comando).</summary>
    internal (string Header, FrameworkElement Page) GetPage(int index)
    {
        var tab = (TabItem)_tabs.Items[index];
        var page = (FrameworkElement)((ScrollViewer)tab.Content).Content;
        return ((string)tab.Header, page);
    }

    internal void SelectPage(int index) => _tabs.SelectedIndex = index;

    /// <summary>La pausa è cambiata dalla scorciatoia o dal menu: la casella segue.</summary>
    public void OnPausedChangedExternally(bool paused)
    {
        if (_pausedCheck is not null && _pausedCheck.IsChecked != paused) _pausedCheck.IsChecked = paused;
    }

    // ---------------------------------------------------------------------------------------------
    // Salvataggio
    // ---------------------------------------------------------------------------------------------

    /// <summary>Scrive i valori dei campi in una copia delle impostazioni correnti; restituisce gli errori trovati.</summary>
    private List<string> CollectInto(AppSettings target)
    {
        var errors = new List<string>();
        foreach (var collect in _collectors)
        {
            try
            {
                var error = collect(target);
                if (error is not null) errors.Add(error);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
        return errors;
    }

    /// <summary>Bozza con i valori della finestra, senza salvarla (per le prove della voce).</summary>
    private AppSettings? BuildDraft()
    {
        var draft = JsonSettingsStore.Clone(_store.Current);
        var errors = CollectInto(draft);
        if (errors.Count > 0)
        {
            ShowStatus("Correggere prima: " + string.Join(" ", errors), error: true);
            return null;
        }
        return draft;
    }

    private void Save()
    {
        var target = JsonSettingsStore.Clone(_store.Current);
        var errors = CollectInto(target);
        if (errors.Count > 0)
        {
            ShowStatus("Non salvato. " + string.Join(" ", errors), error: true);
            return;
        }

        AppSettings saved;
        try
        {
            // I campi della finestra si scrivono sul file riletto dal disco (i collettori sono puri e già validati sopra):
            // ciò che la finestra non mostra, come la chiave, resta quello del file anche se è cambiato da fuori.
            saved = _store.Update(s => CollectInto(s));
        }
        catch (Exception ex)
        {
            ShowStatus("Salvataggio non riuscito: " + ex.Message, error: true);
            return;
        }

        bool startupOk = StartupRegistration.Apply(saved.General.StartWithWindows, _log);
        RefreshProblems();
        var problems = _host.InputProblems;
        string message = "Impostazioni salvate e applicate.";
        if (!startupOk) message += " L'avvio con Windows non è stato aggiornato (vedi il registro).";
        if (problems.Count > 0) message += " Attenzione: " + string.Join(" ", problems);
        ShowStatus(message, error: problems.Count > 0 || !startupOk);
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = error ? Brushes.Firebrick : Brushes.DarkGreen;
    }

    private void RefreshProblems()
    {
        if (_problemsText is null) return;
        var problems = _host.InputProblems;
        _problemsText.Text = problems.Count == 0 ? "Nessun problema." : string.Join(Environment.NewLine, problems.Select(p => "- " + p));
        _problemsText.Foreground = problems.Count == 0 ? Brushes.DarkGreen : Brushes.Firebrick;
    }

    private CancellationToken NewOperationToken(TimeSpan timeout)
    {
        try { _operationCts?.Cancel(); } catch (ObjectDisposedException) { /* ignorato */ }
        _operationCts = new CancellationTokenSource(timeout);
        return _operationCts.Token;
    }

    // ---------------------------------------------------------------------------------------------
    // Scheda Generale
    // ---------------------------------------------------------------------------------------------

    private Panel BuildGeneralTab(AppSettings s)
    {
        var page = Page();

        var start = Section(page, "Avvio");
        string? registered = StartupRegistration.GetRegisteredCommand();
        Check(start, "Avvia Punta e Ascolta con Windows", s.General.StartWithWindows, (t, v) => t.General.StartWithWindows = v,
            registered is null ? "Oggi non è registrato l'avvio automatico."
            : StartupRegistration.IsRegisteredForThisCopy() ? "Registrato per questa copia del programma."
            : $"Registrato per un'altra copia: {registered}. Salvando con la casella attiva si passa a questa.");

        var reading = Section(page, "Lettura");
        _pausedCheck = Check(reading, "In pausa: il pulsante del mouse non legge (le scorciatoie funzionano ancora)",
            s.General.Paused, (t, v) => t.General.Paused = v);

        var diagnostics = Section(page, "Registro");
        Check(diagnostics, "Log dettagliato", s.General.DebugLog, (t, v) => t.General.DebugLog = v,
            "Registra anche il testo letto e i tempi di ogni fase. Serve solo per una diagnosi: poi va spento.");

        var data = Section(page, "Cartelle");
        Note(data, "Impostazioni: " + _store.FilePath);
        Note(data, "Dati (cache audio e log): " + _store.DataDirectory +
                   (_store.IsPortable ? "" : " (la cartella del programma non è scrivibile: si usa AppData)"));
        var buttons = ButtonRow(data);
        AddButton(buttons, "Apri cartella dati", () => _host.OpenFolder(_store.DataDirectory));
        AddButton(buttons, "Apri cartella dei log", _host.OpenLogFolder);

        Note(page, $"Punta e Ascolta {AppServices.Version} ({AppServices.Architecture}).");
        return page;
    }

    // ---------------------------------------------------------------------------------------------
    // Scheda Attivazione
    // ---------------------------------------------------------------------------------------------

    private Panel BuildActivationTab(AppSettings s)
    {
        var page = Page();

        var mouse = Section(page, "Mouse");
        Choice(mouse, "Pulsante che avvia la lettura", new[]
        {
            (TriggerButton.Middle, "Rotellina (clic centrale)"),
            (TriggerButton.X1, "Pulsante laterale indietro"),
            (TriggerButton.X2, "Pulsante laterale avanti"),
            (TriggerButton.None, "Nessuno (solo scorciatoie da tastiera)"),
        }, s.Input.MouseTrigger, (t, v) => t.Input.MouseTrigger = v,
            "Un secondo clic mentre la voce parla la ferma.");
        Check(mouse, "Pressione prolungata: legge tutta la zona attorno al puntatore", s.Input.LongPressEnabled, (t, v) => t.Input.LongPressEnabled = v);
        IntField(mouse, "Durata della pressione prolungata (ms)", s.Input.LongPressMs, 300, 3000, (t, v) => t.Input.LongPressMs = v);
        IntField(mouse, "Anti-rimbalzo (ms)", s.Input.DebounceMs, 0, 2000, (t, v) => t.Input.DebounceMs = v,
            "I clic arrivati entro questo tempo dal precedente vengono ignorati (clic doppi involontari).");
        Check(mouse, "Accetta i clic generati da software (ausili di puntamento, emulatori di mouse)", s.Input.AcceptInjectedEvents,
            (t, v) => t.Input.AcceptInjectedEvents = v);

        var keys = Section(page, "Scorciatoie da tastiera");
        Note(keys, "Esempi: Win+Shift+F9, Ctrl+F8, F8. Campo vuoto = nessuna scorciatoia. Evitare Ctrl+Alt: sulla tastiera italiana è AltGr. " +
            "Senza Ctrl, Alt o Win sono ammessi solo F1-F24, Pausa e Bloc Scorr (un altro tasto verrebbe tolto a tutti i programmi); Esc non si può usare: ferma già la voce mentre parla.");
        HotkeyField(keys, "Leggi sotto il puntatore", s.Input.HotkeyReadAtPointer, (t, v) => t.Input.HotkeyReadAtPointer = v);
        HotkeyField(keys, "Leggi la selezione", s.Input.HotkeyReadSelection, (t, v) => t.Input.HotkeyReadSelection = v);
        HotkeyField(keys, "Ferma la voce", s.Input.HotkeyStop, (t, v) => t.Input.HotkeyStop = v);
        HotkeyField(keys, "Pausa e ripresa", s.Input.HotkeyTogglePause, (t, v) => t.Input.HotkeyTogglePause = v);
        Check(keys, "Esc ferma la voce (solo mentre parla)", s.Input.EscStopsSpeech, (t, v) => t.Input.EscStopsSpeech = v);

        var problems = Section(page, "Problemi di attivazione");
        _problemsText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        problems.Children.Add(_problemsText);
        Note(problems, "Una scorciatoia già usata da un altro programma non funziona: sceglierne un'altra e salvare.");
        RefreshProblems();
        return page;
    }

    // ---------------------------------------------------------------------------------------------
    // Scheda Lettura
    // ---------------------------------------------------------------------------------------------

    private Panel BuildReadingTab(AppSettings s)
    {
        var page = Page();
        var r = s.Reading;

        var what = Section(page, "Che cosa leggere");
        Check(what, "Se il puntatore è dentro un testo selezionato, legge la selezione", r.ReadSelectionWhenPointerInside,
            (t, v) => t.Reading.ReadSelectionWhenPointerInside = v);
        Check(what, "Nei documenti (Word, editor) legge la frase sotto il puntatore", r.ReadSentenceInDocuments,
            (t, v) => t.Reading.ReadSentenceInDocuments = v);
        Check(what, "Per caselle e voci con spunta aggiunge \"attivo\" o \"non attivo\"", r.SpeakToggleState,
            (t, v) => t.Reading.SpeakToggleState = v);
        Check(what, "Non legge le scorciatoie dei menu (Ctrl+N e simili)", r.StripKeyboardShortcuts, (t, v) => t.Reading.StripKeyboardShortcuts = v);
        Check(what, "Non legge emoji ed emoticon (consigliato)", r.StripEmoji, (t, v) => t.Reading.StripEmoji = v);
        IntField(what, "Lunghezza massima di una lettura (caratteri)", r.MaxCharsPerRead, 50, 20000, (t, v) => t.Reading.MaxCharsPerRead = v);

        var messages = Section(page, "Messaggi");
        Check(messages, "Quando non trova testo lo dice", r.SpeakWhenNothingFound, (t, v) => t.Reading.SpeakWhenNothingFound = v);
        TextField(messages, "Frase quando non trova testo", r.NothingFoundText, (t, v) => t.Reading.NothingFoundText = v,
            v => string.IsNullOrWhiteSpace(v) ? "La frase per \"nessun testo\" non può essere vuota." : null);
        TextField(messages, "Frase per le celle vuote", r.EmptyCellText, (t, v) => t.Reading.EmptyCellText = v,
            v => string.IsNullOrWhiteSpace(v) ? "La frase per le celle vuote non può essere vuota." : null);

        var ocrOnly = Section(page, "Programmi letti solo con OCR");
        TextField(ocrOnly, "Nomi dei programmi", string.Join(", ", r.OcrOnlyProcesses), (t, v) => t.Reading.OcrOnlyProcesses = SplitList(v).Select(StripExe).ToList(),
            help: "Nomi dei processi senza .exe, separati da virgole (es. Photoshop). Per questi si salta l'accessibilità.");

        var ocr = Section(page, "Riconoscimento ottico (OCR)");
        Choice(ocr, "Motore", new[]
        {
            (OcrMode.Auto, "Automatico: OCR di Windows, poi ONNX se serve"),
            (OcrMode.WindowsOnly, "Solo OCR di Windows"),
            (OcrMode.OnnxOnly, "Solo OCR ONNX"),
        }, s.Ocr.Mode, (t, v) => t.Ocr.Mode = v);
        Choice(ocr, "Lingua dell'OCR di Windows", OcrLanguages(s.Ocr.WindowsOcrLanguage), s.Ocr.WindowsOcrLanguage, (t, v) => t.Ocr.WindowsOcrLanguage = v);
        IntField(ocr, "Larghezza della zona (pixel al 100%)", s.Ocr.ZoneWidth, 200, 4000, (t, v) => t.Ocr.ZoneWidth = v);
        IntField(ocr, "Altezza della zona (pixel al 100%)", s.Ocr.ZoneHeight, 60, 3000, (t, v) => t.Ocr.ZoneHeight = v);
        DoubleField(ocr, "Distanza massima dalla riga (altezze di riga)", s.Ocr.MaxLineDistanceInLineHeights, 0, 10, (t, v) => t.Ocr.MaxLineDistanceInLineHeights = v);
        Check(ocr, "Unisce le righe vicine in un blocco (cartelli, paragrafi)", s.Ocr.GroupLinesIntoBlocks, (t, v) => t.Ocr.GroupLinesIntoBlocks = v);
        DoubleField(ocr, "Spazio massimo fra righe di un blocco (altezze di riga)", s.Ocr.BlockMaxGapInLineHeights, 0, 5, (t, v) => t.Ocr.BlockMaxGapInLineHeights = v);

        var onnx = _services.OnnxOcr;
        Note(ocr, "OCR di Windows: " + (_services.WindowsOcr.IsAvailable ? "disponibile" : "non disponibile") +
                  ". OCR ONNX: " + (onnx.IsAvailable ? "disponibile" : "non disponibile" + (onnx.UnavailableReason is { } why ? $" ({why})" : "")) + ".");
        return page;
    }

    private static IEnumerable<(string, string)> OcrLanguages(string current)
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var language in global::Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages)
                list.Add((language.LanguageTag, $"{language.DisplayName} ({language.LanguageTag})"));
        }
        catch (Exception)
        {
            // Nessun elenco: resta il valore corrente.
        }
        if (!string.IsNullOrWhiteSpace(current) && list.All(l => !string.Equals(l.Item1, current, StringComparison.OrdinalIgnoreCase)))
            list.Add((current, current + " (non installata)"));
        return list;
    }

    private static IEnumerable<string> SplitList(string text) =>
        text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase);

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    // ---------------------------------------------------------------------------------------------
    // Mattoncini dell'interfaccia
    // ---------------------------------------------------------------------------------------------

    private static StackPanel Page() => new() { Margin = new Thickness(12, 10, 16, 12) };

    private static TabItem Tab(string header, Panel page) => new()
    {
        Header = header,
        Content = new ScrollViewer
        {
            Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        },
    };

    private static StackPanel Section(Panel parent, string title)
    {
        var inner = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        parent.Children.Add(new GroupBox
        {
            Header = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold },
            Content = inner,
            Margin = new Thickness(0, 0, 0, 12),
        });
        return inner;
    }

    private static TextBlock Note(Panel parent, string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 2, 0, 6),
        };
        parent.Children.Add(block);
        return block;
    }

    private static void Row(Panel parent, string label, FrameworkElement control, string? help = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        parent.Children.Add(grid);
        if (help is not null) Note(parent, help);
    }

    private static StackPanel ButtonRow(Panel parent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        parent.Children.Add(row);
        return row;
    }

    private static Button AddButton(Panel row, string text, Action onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        button.Click += (_, _) => onClick();
        row.Children.Add(button);
        return button;
    }

    private CheckBox Check(Panel parent, string label, bool value, Action<AppSettings, bool> apply, string? help = null)
    {
        var box = new CheckBox
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            IsChecked = value,
            Margin = new Thickness(0, 4, 0, 4),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        parent.Children.Add(box);
        if (help is not null) Note(parent, help);
        _collectors.Add(s =>
        {
            apply(s, box.IsChecked == true);
            return null;
        });
        return box;
    }

    private TextBox IntField(Panel parent, string label, int value, int min, int max, Action<AppSettings, int> apply, string? help = null)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        Row(parent, label, box, help);
        _collectors.Add(s =>
        {
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < min || v > max)
                return $"{label}: inserire un numero intero fra {min} e {max}.";
            apply(s, v);
            return null;
        });
        return box;
    }

    private TextBox DoubleField(Panel parent, string label, double value, double min, double max, Action<AppSettings, double> apply, string? help = null)
    {
        var box = new TextBox { Text = value.ToString("0.###", Italian), Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        Row(parent, label, box, help);
        _collectors.Add(s =>
        {
            if (!TryParseDouble(box.Text, out double v) || v < min || v > max)
                return $"{label}: inserire un numero fra {min.ToString(Italian)} e {max.ToString(Italian)}.";
            apply(s, v);
            return null;
        });
        return box;
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private TextBox TextField(Panel parent, string label, string value, Action<AppSettings, string> apply,
        Func<string, string?>? validate = null, string? help = null)
    {
        var box = new TextBox { Text = value ?? "", MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        Row(parent, label, box, help);
        _collectors.Add(s =>
        {
            string v = box.Text.Trim();
            var error = validate?.Invoke(v);
            if (error is not null) return error;
            apply(s, v);
            return null;
        });
        return box;
    }

    private TextBox HotkeyField(Panel parent, string label, string value, Action<AppSettings, string> apply, string? help = null)
    {
        var box = new TextBox { Text = value ?? "", Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        Row(parent, label, box, help);
        _collectors.Add(s =>
        {
            string v = box.Text.Trim();
            if (v.Length > 0)
            {
                if (!HotkeyGesture.TryParse(v, out var gesture) || gesture is null)
                    return $"{label}: scorciatoia non valida (\"{v}\"). Esempio: Win+Shift+F9.";
                if (gesture.Ctrl && gesture.Alt)
                    return $"{label}: evitare Ctrl+Alt, sulla tastiera italiana coincide con AltGr.";
                // Stesso controllo della registrazione: niente Esc, niente tasti senza Ctrl/Alt/Win (salvo F1-F24 e pochi altri).
                if (WindowsInputSource.ValidateHotkey(v) is { } problem)
                    return $"{label}: {problem}.";
            }
            apply(s, v);
            return null;
        });
        return box;
    }

    private Slider SliderField(Panel parent, string label, double value, double min, double max, double step, Func<double, string> format,
        Action<AppSettings, double> apply, string? help = null)
    {
        double start = double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = start,
            SmallChange = step,
            LargeChange = step * 4,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Width = 260,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueText = new TextBlock { Text = format(start), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), MinWidth = 60 };
        slider.ValueChanged += (_, e) => valueText.Text = format(e.NewValue);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(valueText);
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        Row(parent, label, panel, help);
        _collectors.Add(s =>
        {
            apply(s, Math.Round(slider.Value, 3));
            return null;
        });
        return slider;
    }

    private ComboBox Choice<T>(Panel parent, string label, IEnumerable<(T Value, string Text)> items, T current, Action<AppSettings, T> apply,
        string? help = null)
    {
        var combo = new ComboBox { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        FillCombo(combo, items, current);
        Row(parent, label, combo, help);
        _collectors.Add(s =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T value }) apply(s, value);
            return null;
        });
        return combo;
    }

    private static void FillCombo<T>(ComboBox combo, IEnumerable<(T Value, string Text)> items, T current)
    {
        combo.Items.Clear();
        ComboBoxItem? selected = null;
        foreach (var (value, text) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            combo.Items.Add(item);
            if (selected is null && EqualityComparer<T>.Default.Equals(value, current)) selected = item;
        }
        combo.SelectedItem = selected ?? (combo.Items.Count > 0 ? combo.Items[0] : null);
    }

    private static string Percent(double v) => v.ToString("0%", Italian);

    private static string Decimal2(double v) => v.ToString("0.00", Italian);
}
