using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PuntaEAscolta.App.Hosting;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Logging;

namespace PuntaEAscolta.App.SettingsUi;

internal sealed partial class SettingsWindow
{
    private const int DiagnosticLogLines = 200;

    private TextBlock? _lastOutcomeText;
    private TextBlock? _stateText;
    private TextBox? _logBox;

    // ---------------------------------------------------------------------------------------------
    // Scheda Dettatura
    // ---------------------------------------------------------------------------------------------

    private Panel BuildDictationTab(AppSettings s)
    {
        var page = Page();
        var d = s.Dictation;

        var main = Section(page, "Dettatura");
        Check(main, "Attiva la dettatura", d.Enabled, (t, v) => t.Dictation.Enabled = v,
            "Si preme una volta per iniziare a parlare e una seconda volta per finire. La trascrizione usa ElevenLabs: serve la chiave (scheda Voce).");
        HotkeyField(main, "Scorciatoia", d.Hotkey, (t, v) => t.Dictation.Hotkey = v);
        Choice(main, "Pulsante del mouse", new[]
        {
            (TriggerButton.None, "Nessuno"),
            (TriggerButton.X1, "Pulsante laterale indietro"),
            (TriggerButton.X2, "Pulsante laterale avanti"),
            (TriggerButton.Middle, "Rotellina (sconsigliato: è il pulsante di lettura)"),
        }, d.MouseTrigger, (t, v) => t.Dictation.MouseTrigger = v);
        Choice(main, "Microfono", Microphones(d.MicrophoneDeviceId), d.MicrophoneDeviceId ?? "", (t, v) => t.Dictation.MicrophoneDeviceId = v,
            "Con le cuffie scegliere il loro microfono. Se manca si usa quello predefinito.");
        TextField(main, "Lingua (codice, es. it)", d.Language, (t, v) => t.Dictation.Language = v,
            v => v.Length is < 2 or > 10 || v.Any(char.IsWhiteSpace) ? "Lingua della dettatura: usare un codice come it o en." : null);
        Choice(main, "Modello di trascrizione", DictationModels(d.Model), d.Model, (t, v) => t.Dictation.Model = v);
        IntField(main, "Durata massima di una dettatura (secondi)", d.MaxSeconds, 5, 600, (t, v) => t.Dictation.MaxSeconds = v);

        var insert = Section(page, "Inserimento del testo");
        Check(insert, "Rilegge a voce il testo capito prima di inserirlo", d.ReadBack, (t, v) => t.Dictation.ReadBack = v);
        Check(insert, "Dopo la rilettura inserisce da solo (altrimenti serve una nuova pressione entro 15 secondi)", d.AutoInsert,
            (t, v) => t.Dictation.AutoInsert = v, "Una pressione durante la rilettura annulla. Anche fermare la voce annulla.");
        Choice(insert, "Modo di inserimento", new[]
        {
            (DictationInsertMode.Unicode, "Digitazione carattere per carattere (consigliato)"),
            (DictationInsertMode.Paste, "Incolla dagli appunti"),
        }, d.InsertMode, (t, v) => t.Dictation.InsertMode = v);

        var commands = Section(page, "Comandi vocali");
        Note(commands, "Frasi da dire da sole, separate da virgole.");
        TextField(commands, "Cancella l'ultimo testo", string.Join(", ", d.CommandDelete), (t, v) => t.Dictation.CommandDelete = SplitList(v).ToList());
        TextField(commands, "Va a capo", string.Join(", ", d.CommandNewLine), (t, v) => t.Dictation.CommandNewLine = SplitList(v).ToList());
        TextField(commands, "Rilegge l'ultimo testo", string.Join(", ", d.CommandReadAgain), (t, v) => t.Dictation.CommandReadAgain = SplitList(v).ToList());
        return page;
    }

    private IEnumerable<(string, string)> Microphones(string current)
    {
        var list = new List<(string, string)> { ("", "Predefinito di Windows") };
        try
        {
            foreach (var device in _services.Recorder.GetDevices())
                list.Add((device.Id, device.Name + (device.IsDefault ? " (predefinito)" : "")));
        }
        catch (Exception ex)
        {
            _log.Warn($"Elenco dei microfoni non disponibile: {ex.Message}");
        }
        if (!string.IsNullOrWhiteSpace(current) && list.All(m => m.Item1 != current))
            list.Add((current, "Microfono scelto in precedenza (non collegato)"));
        return list;
    }

    private static IEnumerable<(string, string)> DictationModels(string current)
    {
        var list = new List<(string, string)> { ("scribe_v2", "scribe_v2 (consigliato)"), ("scribe_v1", "scribe_v1") };
        if (!string.IsNullOrWhiteSpace(current) && list.All(m => m.Item1 != current)) list.Add((current, current));
        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // Scheda Diagnostica
    // ---------------------------------------------------------------------------------------------

    private Panel BuildDiagnosticsTab()
    {
        var page = Page();

        var last = Section(page, "Ultima lettura");
        _lastOutcomeText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        last.Children.Add(_lastOutcomeText);

        var state = Section(page, "Stato");
        _stateText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        state.Children.Add(_stateText);

        var log = Section(page, $"Registro (ultime {DiagnosticLogLines} righe)");
        _logBox = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Height = 320,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(_logBox, "Registro");
        log.Children.Add(_logBox);
        var buttons = ButtonRow(log);
        AddButton(buttons, "Aggiorna", RefreshDiagnostics);
        AddButton(buttons, "Copia", CopyDiagnostics);
        AddButton(buttons, "Apri cartella dei log", _host.OpenLogFolder);
        return page;
    }

    private void RefreshDiagnostics()
    {
        try
        {
            var settings = _store.Current;
            var (outcome, at) = _host.LastOutcome;
            if (_lastOutcomeText is not null)
            {
                if (outcome is null)
                {
                    _lastOutcomeText.Text = "Nessuna lettura da quando l'app è stata avviata.";
                }
                else
                {
                    string text = settings.General.DebugLog
                        ? (outcome.HasText ? $"\"{outcome.Text}\"" : "(nessun testo)")
                        : "(testo nascosto: attivare il log dettagliato nella scheda Generale per vederlo)";
                    _lastOutcomeText.Text =
                        $"Alle {at:HH:mm:ss}: fonte {outcome.Source}, tipo {outcome.SpeechKind}, lingua {outcome.LanguageHint ?? "?"}, {outcome.ElapsedMs} ms." +
                        Environment.NewLine + "Testo: " + text +
                        Environment.NewLine + "Passaggi: " + outcome.Diagnostics;
                }
            }

            if (_stateText is not null) _stateText.Text = BuildStateText(settings);
            if (_logBox is not null)
            {
                _logBox.Text = ReadLogTail(DiagnosticLogLines);
                var box = _logBox;
                Dispatcher.InvokeAsync(box.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            UpdateCloudState();
            RefreshProblems();
        }
        catch (Exception ex)
        {
            _log.Error("Aggiornamento della diagnostica non riuscito", ex);
        }
    }

    private string BuildStateText(AppSettings settings)
    {
        var sb = new StringBuilder();
        var onnx = _services.OnnxOcr;
        var problems = _host.InputProblems;
        var until = _services.Speech.CloudSuspendedUntil;
        sb.AppendLine($"Punta e Ascolta {AppServices.Version} ({AppServices.Architecture}), impostazioni in {_store.FilePath}.");
        sb.AppendLine($"Lettura: {(_host.Paused ? "in pausa" : "attiva")}, pulsante {TriggerName(settings.Input.MouseTrigger)}, voce che parla: {(_services.Speech.IsSpeaking ? "sì" : "no")}.");
        sb.AppendLine($"Attivazione: {(problems.Count == 0 ? "nessun problema" : string.Join(" ", problems))}");
        sb.AppendLine($"OCR: modo {settings.Ocr.Mode}; Windows {(_services.WindowsOcr.IsAvailable ? "disponibile" : "non disponibile")}; " +
                      $"ONNX {(onnx.IsAvailable ? "disponibile" : "non disponibile" + (onnx.UnavailableReason is { } r ? $" ({r})" : ""))}.");
        sb.AppendLine($"Voce: fornitore {settings.Speech.Provider}; ElevenLabs {(_services.ElevenLabs.IsConfigured ? "configurato" : "non configurato")}" +
                      (until is null ? "" : until.Value == DateTimeOffset.MaxValue ? ", sospeso (chiave rifiutata)" : $", sospeso fino alle {until.Value.ToLocalTime():HH:mm:ss}") + ".");
        sb.Append($"Dettatura: {(settings.Dictation.Enabled ? "attiva" : "spenta")}, stato {_services.Dictation.State}.");
        return sb.ToString();
    }

    private static string TriggerName(TriggerButton button) => button switch
    {
        TriggerButton.Middle => "rotellina",
        TriggerButton.X1 => "laterale indietro",
        TriggerButton.X2 => "laterale avanti",
        _ => "nessuno",
    };

    private string ReadLogTail(int lines)
    {
        try
        {
            _services.Log.Flush();
            string? path = _services.FileLog.CurrentFilePath;
            if (path is null || !File.Exists(path))
            {
                var directory = new DirectoryInfo(_services.FileLog.DirectoryPath);
                path = directory.Exists
                    ? directory.GetFiles(FileLog.FilePrefix + "*" + FileLog.FileExtension).OrderByDescending(f => f.Name).FirstOrDefault()?.FullName
                    : null;
            }
            if (path is null) return "(registro vuoto)";

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - 256 * 1024);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var all = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (start > 0 && all.Count > 0) all.RemoveAt(0); // prima riga probabilmente tagliata
            while (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Count - lines)));
        }
        catch (Exception ex)
        {
            return "Registro non leggibile: " + ex.Message;
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine(_stateText?.Text);
            text.AppendLine();
            text.AppendLine(_lastOutcomeText?.Text);
            text.AppendLine();
            text.AppendLine(_logBox?.Text);
            Clipboard.SetText(text.ToString());
            ShowStatus("Diagnostica copiata negli appunti.", error: false);
        }
        catch (Exception ex)
        {
            ShowStatus("Copia non riuscita: " + ex.Message, error: true);
        }
    }
}
