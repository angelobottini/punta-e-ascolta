using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PuntaEAscolta.App.Hosting;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Settings;
using PuntaEAscolta.Speech;
using PuntaEAscolta.Speech.ElevenLabs;
using PuntaEAscolta.Windows.Audio;

namespace PuntaEAscolta.App.SettingsUi;

internal sealed partial class SettingsWindow
{
    private const string TestPhrase = "Ciao! Sono la voce di Punta e Ascolta. Leggo quello che indichi con il mouse.";

    private static readonly string[] DefaultModels =
    {
        "eleven_multilingual_v2", "eleven_flash_v2_5", "eleven_turbo_v2_5", "eleven_v3",
    };

    private sealed record VoiceChoice(string Id, string Name);

    private PasswordBox? _keyBox;
    private TextBlock? _keyStateText;
    private TextBlock? _accountText;
    private TextBlock? _cloudStateText;
    private TextBlock? _cacheSizeText;
    private ComboBox? _elevenVoiceCombo;
    private ComboBox? _sentenceModelCombo;
    private ComboBox? _labelModelCombo;
    private readonly List<Button> _accountButtons = new();

    private Panel BuildVoiceTab(AppSettings s)
    {
        var page = Page();
        var sp = s.Speech;

        // --- Fornitore e volume ---
        var provider = Section(page, "Fornitore della voce");
        Choice(provider, "Voce da usare", new[]
        {
            (SpeechProviderKind.Auto, "Automatico: ElevenLabs se disponibile, altrimenti Windows"),
            (SpeechProviderKind.ElevenLabs, "ElevenLabs (con ripiego sulla voce di Windows)"),
            (SpeechProviderKind.Windows, "Solo voce di Windows"),
        }, sp.Provider, (t, v) => t.Speech.Provider = v,
            "I messaggi dell'app (\"Nessun testo\"...) usano sempre la voce di Windows.");
        SliderField(provider, "Volume", sp.Volume, 0, 1, 0.05, Percent, (t, v) => t.Speech.Volume = v);
        _cloudStateText = Note(provider, "");
        UpdateCloudState();

        // --- Voce di Windows ---
        var windows = Section(page, "Voce di Windows");
        Choice(windows, "Voce", WindowsVoices(sp.WindowsVoiceName), CurrentWindowsVoice(sp.WindowsVoiceName), (t, v) => t.Speech.WindowsVoiceName = v);
        SliderField(windows, "Velocità", sp.WindowsRate, 0.5, 2.0, 0.05, v => Decimal2(v) + "x", (t, v) => t.Speech.WindowsRate = v);
        var windowsButtons = ButtonRow(windows);
        AddAsyncButton(windowsButtons, "Prova voce di Windows", () => TestVoiceAsync(cloud: false));

        // --- ElevenLabs ---
        var eleven = Section(page, "ElevenLabs");
        _keyStateText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        eleven.Children.Add(_keyStateText);
        UpdateKeyState();

        _keyBox = new PasswordBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        Row(eleven, "Chiave API (incollare qui)", _keyBox, "La chiave viene cifrata con DPAPI: vale solo per questo utente di Windows su questo PC.");
        var keyButtons = ButtonRow(eleven);
        _accountButtons.Add(AddAsyncButton(keyButtons, "Verifica", VerifyAccountAsync));
        AddButton(keyButtons, "Salva chiave", SaveKey);
        AddButton(keyButtons, "Rimuovi chiave", RemoveKey);

        _accountText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
        eleven.Children.Add(_accountText);

        _elevenVoiceCombo = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var currentVoice = new VoiceChoice(sp.ElevenLabsVoiceId ?? "", sp.ElevenLabsVoiceName ?? "");
        FillCombo(_elevenVoiceCombo, new[]
        {
            string.IsNullOrWhiteSpace(currentVoice.Id)
                ? (currentVoice, "(nessuna voce scelta: premere Verifica per l'elenco)")
                : (currentVoice, string.IsNullOrWhiteSpace(currentVoice.Name) ? currentVoice.Id : currentVoice.Name),
        }, currentVoice);
        Row(eleven, "Voce ElevenLabs", _elevenVoiceCombo);
        _collectors.Add(t =>
        {
            if (_elevenVoiceCombo.SelectedItem is ComboBoxItem { Tag: VoiceChoice v })
            {
                t.Speech.ElevenLabsVoiceId = v.Id;
                t.Speech.ElevenLabsVoiceName = v.Name;
            }
            return null;
        });
        var voiceButtons = ButtonRow(eleven);
        _accountButtons.Add(AddAsyncButton(voiceButtons, "Aggiorna elenco voci", VerifyAccountAsync));
        AddAsyncButton(voiceButtons, "Prova voce ElevenLabs", () => TestVoiceAsync(cloud: true));

        _sentenceModelCombo = ModelCombo(eleven, "Modello per frasi e testi", sp.ElevenLabsSentenceModel, allowEmpty: false,
            (t, v) => t.Speech.ElevenLabsSentenceModel = v);
        _labelModelCombo = ModelCombo(eleven, "Modello per etichette brevi", sp.ElevenLabsLabelModel, allowEmpty: true,
            (t, v) => t.Speech.ElevenLabsLabelModel = v, "Vuoto = stesso modello delle frasi. Il modello Flash è il più rapido.");
        Choice(eleven, "Formato audio", new[]
        {
            ("pcm_44100", "PCM 44,1 kHz (piano Pro)"),
            ("pcm_24000", "PCM 24 kHz"),
            ("pcm_22050", "PCM 22,05 kHz"),
            ("pcm_16000", "PCM 16 kHz"),
        }, sp.ElevenLabsOutputFormat, (t, v) => t.Speech.ElevenLabsOutputFormat = v);
        SliderField(eleven, "Stabilità", sp.ElevenLabsStability, 0, 1, 0.05, Decimal2, (t, v) => t.Speech.ElevenLabsStability = v);
        SliderField(eleven, "Somiglianza", sp.ElevenLabsSimilarity, 0, 1, 0.05, Decimal2, (t, v) => t.Speech.ElevenLabsSimilarity = v);
        SliderField(eleven, "Stile", sp.ElevenLabsStyle, 0, 1, 0.05, Decimal2, (t, v) => t.Speech.ElevenLabsStyle = v);
        SliderField(eleven, "Velocità", sp.ElevenLabsSpeed, 0.7, 1.2, 0.05, v => Decimal2(v) + "x", (t, v) => t.Speech.ElevenLabsSpeed = v);
        Choice(eleven, "Lingua delle etichette", new[]
        {
            (LabelLanguageMode.Auto, "Automatica (italiano o inglese secondo il testo)"),
            (LabelLanguageMode.Italian, "Sempre italiano"),
            (LabelLanguageMode.English, "Sempre inglese"),
        }, sp.LabelLanguage, (t, v) => t.Speech.LabelLanguage = v);
        IntField(eleven, "Attesa massima per le etichette (ms)", sp.FirstAudioTimeoutLabelMs, 300, 30000, (t, v) => t.Speech.FirstAudioTimeoutLabelMs = v);
        IntField(eleven, "Attesa massima per le frasi (ms)", sp.FirstAudioTimeoutSentenceMs, 300, 30000, (t, v) => t.Speech.FirstAudioTimeoutSentenceMs = v,
            "Se ElevenLabs non risponde entro questo tempo si legge subito con la voce di Windows.");

        // --- Riservatezza ---
        var privacy = Section(page, "Riservatezza");
        Check(privacy, "Il testo dei documenti si legge solo con la voce di Windows (non lascia il PC)", sp.DocumentsWithLocalVoiceOnly,
            (t, v) => t.Speech.DocumentsWithLocalVoiceOnly = v);

        // --- Cache ---
        var cache = Section(page, "Cache audio");
        Check(cache, "Conserva l'audio di ElevenLabs già scaricato (letture ripetute immediate e senza crediti)", sp.CacheEnabled,
            (t, v) => t.Speech.CacheEnabled = v);
        IntField(cache, "Dimensione massima (MB)", sp.CacheMaxMegabytes, 10, 20000, (t, v) => t.Speech.CacheMaxMegabytes = v);
        _cacheSizeText = Note(cache, "Dimensione attuale: calcolo...");
        var cacheButtons = ButtonRow(cache);
        AddAsyncButton(cacheButtons, "Svuota cache", ClearCacheAsync);
        return page;
    }

    private void StartInitialLoads()
    {
        _ = UpdateCacheSizeAsync();
        // Con una chiave già salvata si caricano subito crediti e voci (senza bloccare la finestra).
        if (CommandLine.CommandLineRunner.ReadApiKey(_store.Current, _services.Protector).Key is not null)
        {
            _ = VerifyAccountAsync();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Voci e modelli
    // ---------------------------------------------------------------------------------------------

    private static IEnumerable<(string, string)> WindowsVoices(string current)
    {
        var list = new List<(string, string)> { ("", "Predefinita (prima voce italiana installata)") };
        foreach (var voice in WindowsVoiceSynthesizer.GetVoices())
        {
            string gender = voice.Gender switch { "Female" => "femminile", "Male" => "maschile", _ => voice.Gender };
            list.Add((voice.DisplayName, $"{voice.DisplayName} ({voice.Language}, {gender})"));
        }
        string selected = CurrentWindowsVoice(current);
        if (!string.IsNullOrWhiteSpace(selected) && list.All(v => v.Item1 != selected))
            list.Add((selected, selected + " (non installata)"));
        return list;
    }

    /// <summary>Il file può contenere l'Id, il nome completo o una parte ("Elsa"): si riporta al nome mostrato.</summary>
    private static string CurrentWindowsVoice(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return "";
        var voices = WindowsVoiceSynthesizer.GetVoices();
        var match = voices.FirstOrDefault(v => string.Equals(v.Id, configured, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(v.DisplayName, configured, StringComparison.OrdinalIgnoreCase))
                    ?? voices.FirstOrDefault(v => v.DisplayName.Contains(configured, StringComparison.OrdinalIgnoreCase));
        return match?.DisplayName ?? configured;
    }

    private ComboBox ModelCombo(Panel parent, string label, string current, bool allowEmpty, Action<AppSettings, string> apply, string? help = null)
    {
        var combo = new ComboBox { IsEditable = true, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var model in DefaultModels) combo.Items.Add(model);
        if (!string.IsNullOrWhiteSpace(current) && !DefaultModels.Contains(current)) combo.Items.Add(current);
        combo.Text = current ?? "";
        Row(parent, label, combo, help);
        _collectors.Add(t =>
        {
            string v = (combo.Text ?? "").Trim();
            if (v.Length == 0 && !allowEmpty) return $"{label}: indicare un modello.";
            if (v.Any(char.IsWhiteSpace)) return $"{label}: il nome del modello non può contenere spazi.";
            apply(t, v);
            return null;
        });
        return combo;
    }

    private static void AddModels(ComboBox? combo, IEnumerable<string> models)
    {
        if (combo is null) return;
        string text = combo.Text;
        foreach (var model in models)
        {
            if (!combo.Items.Cast<object>().Any(i => string.Equals(i as string, model, StringComparison.OrdinalIgnoreCase)))
                combo.Items.Add(model);
        }
        combo.Text = text;
    }

    // ---------------------------------------------------------------------------------------------
    // Chiave e account
    // ---------------------------------------------------------------------------------------------

    private void UpdateKeyState()
    {
        if (_keyStateText is null) return;
        var (_, state) = CommandLine.CommandLineRunner.ReadApiKey(_store.Current, _services.Protector);
        _keyStateText.Text = state switch
        {
            "presente" => "Chiave salvata.",
            "assente" => "Nessuna chiave salvata: si usa la voce di Windows.",
            _ => "La chiave salvata non è leggibile su questo PC (cartella copiata da un altro computer o utente): va reinserita.",
        };
        _keyStateText.Foreground = state == "presente" ? Brushes.DarkGreen : Brushes.Firebrick;
    }

    private void UpdateCloudState()
    {
        if (_cloudStateText is null) return;
        var until = _services.Speech.CloudSuspendedUntil;
        string text;
        if (!_services.ElevenLabs.IsConfigured)
            text = "ElevenLabs non configurato (servono la chiave e una voce): si usa la voce di Windows.";
        else if (until is null)
            text = "ElevenLabs configurato e disponibile.";
        else if (until.Value == DateTimeOffset.MaxValue)
            text = "ElevenLabs sospeso: la chiave è stata rifiutata. Si riprova dopo il prossimo salvataggio delle impostazioni.";
        else
            text = $"ElevenLabs sospeso fino alle {until.Value.ToLocalTime():HH:mm:ss} per errori ripetuti (rete, server o crediti).";
        _cloudStateText.Text = text;
    }

    /// <summary>Chiave per le chiamate all'account: quella digitata, altrimenti quella salvata.</summary>
    private string? KeyForAccount()
    {
        string typed = _keyBox?.Password.Trim() ?? "";
        if (typed.Length > 0) return typed;
        return CommandLine.CommandLineRunner.ReadApiKey(_store.Current, _services.Protector).Key;
    }

    private async Task VerifyAccountAsync()
    {
        if (_accountText is null) return;
        string? key = KeyForAccount();
        if (key is null)
        {
            _accountText.Text = "Incollare la chiave nel campo qui sopra, oppure salvarne una.";
            _accountText.Foreground = Brushes.Firebrick;
            return;
        }

        foreach (var b in _accountButtons) b.IsEnabled = false;
        _accountText.Foreground = SystemColors.ControlTextBrush;
        _accountText.Text = "Verifica in corso...";
        var ct = NewOperationToken(TimeSpan.FromSeconds(30));
        try
        {
            var client = new ElevenLabsAccountClient(_services.Http);
            bool valid = await client.ValidateKeyAsync(key, ct);
            if (!valid)
            {
                _accountText.Text = "ElevenLabs ha rifiutato la chiave. Controllare di averla copiata per intero.";
                _accountText.Foreground = Brushes.Firebrick;
                return;
            }

            var subscription = await client.GetSubscriptionAsync(key, ct);
            string reset = subscription.NextReset is { } r ? $", si rinnova il {r.ToLocalTime().ToString("d MMMM yyyy", Italian)}" : "";
            _accountText.Text = string.Format(Italian,
                "Chiave valida. Piano {0} ({1}). Caratteri usati {2:N0} su {3:N0}, residui {4:N0}{5}.",
                subscription.Tier, subscription.Status, subscription.CharacterCount, subscription.CharacterLimit,
                subscription.CharactersRemaining, reset);
            _accountText.Foreground = Brushes.DarkGreen;

            var voices = await client.GetVoicesAsync(key, ct);
            if (_elevenVoiceCombo is not null)
            {
                var current = _elevenVoiceCombo.SelectedItem is ComboBoxItem { Tag: VoiceChoice v } ? v : new VoiceChoice("", "");
                var items = voices.Select(voice => (new VoiceChoice(voice.VoiceId, voice.Name), voice.DisplayName)).ToList();
                var selected = items.Select(i => i.Item1).FirstOrDefault(v => v.Id == current.Id);
                if (selected is null)
                {
                    items.Insert(0, (current, string.IsNullOrWhiteSpace(current.Id) ? "(nessuna voce scelta)" : current.Name + " (non trovata nell'account)"));
                    selected = current;
                }
                FillCombo(_elevenVoiceCombo, items, selected);
            }

            try
            {
                var models = await client.GetModelsAsync(key, ct);
                var ids = models.Where(m => m.SupportsItalian || m.Languages.Count == 0).Select(m => m.ModelId).ToList();
                AddModels(_sentenceModelCombo, ids);
                AddModels(_labelModelCombo, ids);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"Elenco dei modelli ElevenLabs non disponibile: {ex.Message}");
            }

            _accountText.Text += $" {voices.Count} voci nell'account.";
        }
        catch (OperationCanceledException)
        {
            _accountText.Text = "Verifica interrotta (tempo scaduto o finestra chiusa).";
            _accountText.Foreground = Brushes.Firebrick;
        }
        catch (Exception ex)
        {
            _accountText.Text = "Verifica non riuscita: " + ex.Message;
            _accountText.Foreground = Brushes.Firebrick;
        }
        finally
        {
            foreach (var b in _accountButtons) b.IsEnabled = true;
            UpdateCloudState();
        }
    }

    private void SaveKey()
    {
        string typed = _keyBox?.Password.Trim() ?? "";
        if (typed.Length == 0)
        {
            ShowStatus("Incollare prima la chiave nel campo \"Chiave API\".", error: true);
            return;
        }
        if (typed.Length > 256 || typed.Any(char.IsWhiteSpace) || typed.Any(char.IsControl))
        {
            ShowStatus("La chiave incollata contiene spazi o caratteri non validi.", error: true);
            return;
        }

        try
        {
            string protectedKey = _services.Protector.Protect(typed);
            _store.Update(s => s.Speech.ElevenLabsProtectedApiKey = protectedKey);
            _keyBox?.Clear();
            _log.Info("Chiave ElevenLabs salvata dalla finestra impostazioni");
            ShowStatus("Chiave salvata (cifrata). Scegliere la voce e premere Salva per le altre modifiche.", error: false);
        }
        catch (Exception ex)
        {
            ShowStatus("Salvataggio della chiave non riuscito: " + ex.Message, error: true);
        }
        UpdateKeyState();
        UpdateCloudState();
    }

    private void RemoveKey()
    {
        try
        {
            if (string.IsNullOrEmpty(_store.Current.Speech.ElevenLabsProtectedApiKey))
            {
                ShowStatus("Nessuna chiave da rimuovere.", error: false);
                return;
            }
            _store.Update(s => s.Speech.ElevenLabsProtectedApiKey = "");
            _log.Info("Chiave ElevenLabs rimossa dalla finestra impostazioni");
            ShowStatus("Chiave rimossa: si userà la voce di Windows.", error: false);
        }
        catch (Exception ex)
        {
            ShowStatus("Rimozione della chiave non riuscita: " + ex.Message, error: true);
        }
        UpdateKeyState();
        UpdateCloudState();
    }

    // ---------------------------------------------------------------------------------------------
    // Prova della voce (con i valori della finestra, anche non salvati)
    // ---------------------------------------------------------------------------------------------

    private async Task TestVoiceAsync(bool cloud)
    {
        var draft = BuildDraft();
        if (draft is null) return;

        if (cloud)
        {
            string typed = _keyBox?.Password.Trim() ?? "";
            if (typed.Length > 0) draft.Speech.ElevenLabsProtectedApiKey = _services.Protector.Protect(typed);
        }

        var scoped = new ScopedSettingsStore(_store, draft);
        ISpeechSynthesizer synthesizer = cloud
            ? new ElevenLabsSynthesizer(_services.Http, scoped, _services.Protector, _log)
            : new WindowsVoiceSynthesizer(scoped, _log);
        var ct = NewOperationToken(TimeSpan.FromSeconds(60));
        try
        {
            if (cloud && !synthesizer.IsConfigured)
            {
                ShowStatus("Per provare ElevenLabs servono la chiave (incollata o salvata) e una voce scelta dall'elenco.", error: true);
                return;
            }

            _services.Speech.Stop();
            ShowStatus(cloud ? "Prova della voce ElevenLabs..." : "Prova della voce di Windows...", error: false);
            var request = new SpeechRequest(TestPhrase, SpeechKind.Sentence, "it");
            await using var audio = await synthesizer.SynthesizeAsync(request, ct);
            double volume = double.IsFinite(draft.Speech.Volume) ? Math.Clamp(draft.Speech.Volume, 0, 1) : 1;
            await _services.Player.PlayAsync(audio.Pcm, audio.Format, volume, ct);
            ShowStatus("Prova terminata.", error: false);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Prova interrotta.", error: false);
        }
        catch (SpeechProviderException ex)
        {
            ShowStatus("Prova non riuscita: " + ex.Message, error: true);
        }
        catch (Exception ex)
        {
            _log.Error("Prova della voce non riuscita", ex);
            ShowStatus("Prova non riuscita: " + ex.Message, error: true);
        }
        finally
        {
            (synthesizer as IDisposable)?.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Cache
    // ---------------------------------------------------------------------------------------------

    private async Task UpdateCacheSizeAsync()
    {
        if (_cacheSizeText is null) return;
        try
        {
            long bytes = await Task.Run(() => _services.Cache.GetSizeBytes());
            _cacheSizeText.Text = string.Format(Italian, "Dimensione attuale: {0:0.0} MB in {1}", bytes / 1048576.0, _services.Cache.Directory);
        }
        catch (Exception ex)
        {
            _cacheSizeText.Text = "Dimensione non disponibile: " + ex.Message;
        }
    }

    private async Task ClearCacheAsync()
    {
        try
        {
            await Task.Run(() => _services.Cache.Clear());
            _log.Info("Cache audio svuotata dalla finestra impostazioni");
            ShowStatus("Cache svuotata.", error: false);
        }
        catch (Exception ex)
        {
            ShowStatus("Svuotamento non riuscito: " + ex.Message, error: true);
        }
        await UpdateCacheSizeAsync();
    }

    private Button AddAsyncButton(Panel row, string text, Func<Task> action)
    {
        Button? button = null;
        button = AddButton(row, text, async () =>
        {
            if (button is null) return;
            button.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _log.Error($"Pulsante \"{text}\" non riuscito", ex);
                ShowStatus(ex.Message, error: true);
            }
            finally
            {
                button.IsEnabled = true;
            }
        });
        return button;
    }
}
