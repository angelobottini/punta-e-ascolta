using System.Diagnostics;
using PuntaEAscolta.App.Hosting;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Reading;
using PuntaEAscolta.Logic.Resolution;
using PuntaEAscolta.Logic.Settings;
using PuntaEAscolta.Logic.Text;
using PuntaEAscolta.Ocr.Onnx;
using PuntaEAscolta.Speech;
using PuntaEAscolta.Speech.ElevenLabs;
using PuntaEAscolta.Windows.Audio;
using PuntaEAscolta.Windows.Input;
using PuntaEAscolta.Windows.Ocr;

namespace PuntaEAscolta.App.CommandLine;

/// <summary>Modalità di prova senza interfaccia: ogni comando stampa un oggetto JSON su stdout.</summary>
internal static class CommandLineRunner
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SpeakTimeout = TimeSpan.FromMinutes(5);

    public static int Run(string[] args)
    {
        var parsed = CommandLineArguments.Parse(args, out string? error);
        if (parsed is null)
        {
            CliJson.Print(CliJson.Error("argomenti", error ?? "Argomenti non validi."));
            return 1;
        }

        if (parsed.Command == CliCommand.Help)
        {
            Console.Out.Write(HelpText.Text);
            Console.Out.Flush();
            return 0;
        }

        string name = CommandName(parsed.Command);
        using var log = new AppLog { MirrorToStandardError = parsed.Verbose, ForceDebug = parsed.Verbose };
        try
        {
            // L'anteprima della finestra impostazioni usa WPF: resta sul thread STA principale.
            if (parsed.Command == CliCommand.PreviewSettings) return SettingsPreview.Run(parsed.File!, name, log);

            // Lavoro asincrono fuori dal thread STA principale (nessun contesto di sincronizzazione da bloccare).
            return Task.Run(() => RunAsync(parsed, name, log)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Error($"Comando {name} non riuscito", ex);
            CliJson.Print(CliJson.Error(name, $"{ex.GetType().Name}: {ex.Message}"));
            return 1;
        }
    }

    private static string CommandName(CliCommand command) => command switch
    {
        CliCommand.ReadAt => "read-at",
        CliCommand.ReadSelection => "read-selection",
        CliCommand.OcrFile => "ocr-file",
        CliCommand.Speak => "speak",
        CliCommand.Voices => "voices",
        CliCommand.SelfTest => "selftest",
        CliCommand.SetKey => "set-key",
        CliCommand.Exit => "exit",
        CliCommand.PreviewSettings => "preview-settings",
        _ => "help",
    };

    private static async Task<int> RunAsync(CommandLineArguments a, string name, AppLog log)
    {
        if (a.DelayMs > 0) await Task.Delay(a.DelayMs).ConfigureAwait(false);

        return a.Command switch
        {
            CliCommand.ReadAt => await ReadAsync(a, name, log, selection: false).ConfigureAwait(false),
            CliCommand.ReadSelection => await ReadAsync(a, name, log, selection: true).ConfigureAwait(false),
            CliCommand.OcrFile => await OcrFileAsync(a, name, log).ConfigureAwait(false),
            CliCommand.Speak => await SpeakAsync(a, name, log).ConfigureAwait(false),
            CliCommand.Voices => await VoicesAsync(name, log).ConfigureAwait(false),
            CliCommand.SelfTest => await SelfTest.RunAsync(name, log).ConfigureAwait(false),
            CliCommand.SetKey => await SetKeyAsync(a, name, log).ConfigureAwait(false),
            CliCommand.Exit => await ExitRunningAppAsync(name, log).ConfigureAwait(false),
            _ => 1,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // --read-at, --read-selection
    // ---------------------------------------------------------------------------------------------

    private static async Task<int> ReadAsync(CommandLineArguments a, string name, AppLog log, bool selection)
    {
        using var services = AppServices.Create(log, new AppServicesOptions { MeasureSpeech = a.SpeakResult });
        var point = a.Point ?? NativePointer.GetPhysicalPosition();
        var kind = selection ? ReadRequestKind.Selection : a.Zone ? ReadRequestKind.ZoneAroundPointer : ReadRequestKind.AtPointer;
        log.Info($"Riga di comando: {name} {kind} a {point.X},{point.Y}");

        var stopwatch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(ResolveTimeout);
        var outcome = await services.Resolver.ResolveAsync(new ReadRequest(kind, point), cts.Token).ConfigureAwait(false);
        long resolveMs = stopwatch.ElapsedMilliseconds;

        object? spoken = null;
        if (a.SpeakResult)
        {
            var reading = services.Settings.Current.Reading;
            SpeechRequest? request = outcome.HasText
                ? BuildRequest(outcome.Text, outcome.SpeechKind, outcome.LanguageHint, outcome.Sensitive)
                : reading.SpeakWhenNothingFound && !string.IsNullOrWhiteSpace(reading.NothingFoundText)
                    ? new SpeechRequest(reading.NothingFoundText.Trim(), SpeechKind.System, "it")
                    : null;
            spoken = request is null ? new { skipped = true } : await SpeakRequestAsync(services, request, warmUp: false).ConfigureAwait(false);
        }

        CliJson.Print(new
        {
            command = name,
            ok = true,
            point = CliJson.Point(point),
            dpiScale = SafeDpi(services.Capture, point),
            kind,
            hasText = outcome.HasText,
            source = outcome.Source,
            text = outcome.Text,
            speechKind = outcome.SpeechKind,
            language = outcome.LanguageHint,
            sensitive = outcome.Sensitive,
            elapsedMs = outcome.ElapsedMs,
            totalMs = resolveMs,
            diagnostics = outcome.Diagnostics,
            spoken,
        });
        return 0;
    }

    private static double? SafeDpi(IScreenCapture capture, ScreenPoint point)
    {
        try { return capture.GetDpiScale(point); }
        catch (Exception) { return null; }
    }

    /// <summary>Come l'orchestratore: testo spezzato in frasi da massimo 400 caratteri.</summary>
    private static SpeechRequest BuildRequest(string text, SpeechKind kind, string? language, bool sensitive)
    {
        text = text.Trim();
        var chunks = SentenceSplitter.SplitSentences(text, ReadOrchestrator.MaxChunkChars)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
        if (chunks.Count == 0) chunks.Add(text);
        return new SpeechRequest(text, kind, language, sensitive) { Chunks = chunks };
    }

    private static async Task<object> SpeakRequestAsync(AppServices services, SpeechRequest request, bool warmUp)
    {
        long warmUpMs = 0;
        if (warmUp)
        {
            // Come all'avvio dell'app: motore audio e voce locale pronti prima della prima lettura.
            var warm = Stopwatch.StartNew();
            await Task.WhenAll(services.Player.WarmUpAsync(), services.WindowsVoice.WarmUpAsync()).ConfigureAwait(false);
            warmUpMs = warm.ElapsedMilliseconds;
        }

        services.MeasuredPlayer?.Reset();
        services.MeasuredLocalVoice?.Reset();
        long start = Stopwatch.GetTimestamp();
        using var cts = new CancellationTokenSource(SpeakTimeout);
        SpeechOutcome outcome;
        try
        {
            outcome = await services.Speech.SpeakWithOutcomeAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            services.Speech.Stop();
            outcome = SpeechOutcome.Stopped;
        }
        long end = Stopwatch.GetTimestamp();

        long firstPlay = services.MeasuredPlayer?.FirstPlayTimestamp ?? 0;
        bool local = (services.MeasuredLocalVoice?.Calls ?? 0) > 0;
        int plays = services.MeasuredPlayer?.PlayCount ?? 0;
        string voice = plays == 0 ? "nessuna" : local ? "windows" : "elevenlabs-o-cache";
        return new
        {
            outcome,
            voice,
            chunks = request.Chunks?.Count ?? 1,
            warmUpMs = warmUp ? warmUpMs : (long?)null,
            playStartMs = firstPlay > 0 ? Math.Round(Stopwatch.GetElapsedTime(start, firstPlay).TotalMilliseconds, 1) : (double?)null,
            totalMs = Math.Round(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, 1),
            cloudConfigured = services.ElevenLabs.IsConfigured,
            cloudSuspendedUntil = services.Speech.CloudSuspendedUntil,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // --speak
    // ---------------------------------------------------------------------------------------------

    private static async Task<int> SpeakAsync(CommandLineArguments a, string name, AppLog log)
    {
        Action<AppSettings>? overrides = a.Provider switch
        {
            "windows" => s => s.Speech.Provider = SpeechProviderKind.Windows,
            "elevenlabs" => s => s.Speech.Provider = SpeechProviderKind.ElevenLabs,
            "auto" => s => s.Speech.Provider = SpeechProviderKind.Auto,
            _ => null,
        };
        using var services = AppServices.Create(log, new AppServicesOptions { Overrides = overrides, MeasureSpeech = true });
        var settings = services.Settings.Current;

        string text = (a.Text ?? "").Trim();
        if (settings.Reading.StripEmoji) text = EmojiFilter.Strip(text).Trim();
        if (text.Length == 0)
        {
            CliJson.Print(CliJson.Error(name, "Testo vuoto: niente da pronunciare."));
            return 1;
        }

        string? language = LanguageGuesser.Guess(text);
        var request = BuildRequest(text, SpeechKind.Sentence, language, sensitive: false);
        log.Info($"Riga di comando: speak, {text.Length} caratteri, fornitore {settings.Speech.Provider}");
        var result = await SpeakRequestAsync(services, request, warmUp: true).ConfigureAwait(false);

        CliJson.Print(new
        {
            command = name,
            ok = true,
            text,
            language,
            provider = settings.Speech.Provider,
            volume = settings.Speech.Volume,
            windowsVoice = settings.Speech.WindowsVoiceName,
            result,
        });
        return 0;
    }

    // ---------------------------------------------------------------------------------------------
    // --ocr-file
    // ---------------------------------------------------------------------------------------------

    private static async Task<int> OcrFileAsync(CommandLineArguments a, string name, AppLog log)
    {
        string path = Path.GetFullPath(a.File!);
        if (!File.Exists(path))
        {
            CliJson.Print(CliJson.Error(name, $"File non trovato: {path}"));
            return 1;
        }

        var store = AppServices.OpenSettings(log);
        var settings = store.Current;
        double scale = a.Scale ?? CurrentDpiScale();
        var image = PngLoader.Load(path, scale);
        var point = a.Point!.Value;

        var engines = new List<IOcrEngine>();
        try
        {
            if (a.Engine is "windows" or "entrambi") engines.Add(new WindowsOcrEngine(() => settings.Ocr.WindowsOcrLanguage, log));
            if (a.Engine is "onnx" or "entrambi") engines.Add(new OnnxOcrEngine(log));

            var results = new List<object>();
            foreach (var engine in engines)
            {
                results.Add(await RunEngineAsync(engine, image, point, settings).ConfigureAwait(false));
            }

            CliJson.Print(new
            {
                command = name,
                ok = true,
                file = path,
                width = image.Width,
                height = image.Height,
                dpiScale = scale,
                point = CliJson.Point(point),
                results,
            });
            return 0;
        }
        finally
        {
            foreach (var engine in engines)
            {
                try { engine.Dispose(); } catch (Exception ex) { log.Error("Chiusura del motore OCR", ex); }
            }
        }
    }

    private static double CurrentDpiScale()
    {
        try { return new GdiScreenCapture().GetDpiScale(NativePointer.GetPhysicalPosition()); }
        catch (Exception) { return 1.0; }
    }

    private static async Task<object> RunEngineAsync(IOcrEngine engine, CapturedImage image, ScreenPoint point, AppSettings settings)
    {
        if (!engine.IsAvailable)
        {
            return new
            {
                engine = engine.Name,
                available = false,
                reason = (engine as OnnxOcrEngine)?.UnavailableReason,
            };
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stopwatch = Stopwatch.StartNew();
        var full = await engine.RecognizeAsync(image, cts.Token).ConfigureAwait(false);
        long fullMs = stopwatch.ElapsedMilliseconds;
        var fullTimings = (engine as OnnxOcrEngine)?.LastTimings;
        var selection = PointerTextSelector.Select(full, point.X, point.Y, settings.Ocr, wholeZone: false);

        object? pointPass = null;
        if (engine is IPointOcrEngine pointEngine)
        {
            stopwatch.Restart();
            var near = await pointEngine.RecognizeAroundPointAsync(image, point.X, point.Y, cts.Token).ConfigureAwait(false);
            long nearMs = stopwatch.ElapsedMilliseconds;
            var nearSelection = PointerTextSelector.Select(near, point.X, point.Y, settings.Ocr, wholeZone: false);
            pointPass = new
            {
                elapsedMs = nearMs,
                timings = (engine as OnnxOcrEngine)?.LastTimings,
                lines = CliJson.Lines(near),
                selection = Selection(nearSelection),
            };
        }

        return new
        {
            engine = engine.Name,
            available = true,
            elapsedMs = fullMs,
            timings = fullTimings,
            lineCount = full.Lines.Count,
            lines = CliJson.Lines(full),
            selection = Selection(selection),
            pointPass,
        };
    }

    private static object? Selection(OcrSelection? s) =>
        s is null ? null : new { source = s.Source, text = s.Text, lineCount = s.LineCount };

    // ---------------------------------------------------------------------------------------------
    // --voices
    // ---------------------------------------------------------------------------------------------

    private static async Task<int> VoicesAsync(string name, AppLog log)
    {
        var store = AppServices.OpenSettings(log);
        var speech = store.Current.Speech;
        var windows = WindowsVoiceSynthesizer.GetVoices()
            .Select(v => new { id = v.Id, name = v.DisplayName, language = v.Language, gender = v.Gender })
            .ToArray();

        object elevenLabs;
        var (key, keyState) = ReadApiKey(store.Current, new DpapiSecretProtector());
        if (key is null)
        {
            elevenLabs = new { configured = false, keyState };
        }
        else
        {
            using var http = AppServices.CreateHttpClient();
            var client = new ElevenLabsAccountClient(http);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var voices = await client.GetVoicesAsync(key, cts.Token).ConfigureAwait(false);
                elevenLabs = new
                {
                    configured = true,
                    keyState,
                    voices = voices.Select(v => new
                    {
                        id = v.VoiceId,
                        name = v.Name,
                        description = v.DisplayName,
                        category = v.Category,
                        language = v.Language ?? v.Accent,
                        gender = v.Gender,
                    }).ToArray(),
                };
            }
            catch (Exception ex) when (ex is SpeechProviderException or OperationCanceledException or HttpRequestException)
            {
                elevenLabs = new { configured = true, keyState, error = ex.Message };
            }
        }

        CliJson.Print(new
        {
            command = name,
            ok = true,
            current = new
            {
                provider = speech.Provider,
                windowsVoiceName = speech.WindowsVoiceName,
                windowsRate = speech.WindowsRate,
                elevenLabsVoiceId = speech.ElevenLabsVoiceId,
                elevenLabsVoiceName = speech.ElevenLabsVoiceName,
            },
            windows,
            elevenLabs,
        });
        return 0;
    }

    /// <summary>Chiave in chiaro (solo in memoria) e stato: "assente", "presente", "non decifrabile".</summary>
    internal static (string? Key, string State) ReadApiKey(AppSettings settings, ISecretProtector protector)
    {
        string protectedKey = settings.Speech.ElevenLabsProtectedApiKey;
        if (string.IsNullOrWhiteSpace(protectedKey)) return (null, "assente");
        string? key = protector.Unprotect(protectedKey);
        return string.IsNullOrWhiteSpace(key) ? (null, "non decifrabile su questo PC o per questo utente") : (key, "presente");
    }

    // ---------------------------------------------------------------------------------------------
    // --exit
    // ---------------------------------------------------------------------------------------------

    /// <summary>Chiede all'app con l'icona di notifica di chiudersi e attende che abbia finito (mutex rilasciato).</summary>
    private static async Task<int> ExitRunningAppAsync(string name, AppLog log)
    {
        if (!SingleInstance.IsRunning())
        {
            CliJson.Print(new { command = name, ok = true, wasRunning = false, closed = true, message = "L'app non era in esecuzione." });
            return 0;
        }

        log.Info("Riga di comando: chiusura dell'app in esecuzione");
        var stopwatch = Stopwatch.StartNew();
        bool signalled = SingleInstance.SignalExit();
        bool closed = false;
        while (signalled && stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (!SingleInstance.IsRunning())
            {
                closed = true;
                break;
            }
        }

        CliJson.Print(new
        {
            command = name,
            ok = closed,
            wasRunning = true,
            closed,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            message = closed ? "App chiusa." : "L'app non si è chiusa entro 15 secondi.",
        });
        return closed ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------
    // --set-key
    // ---------------------------------------------------------------------------------------------

    private static async Task<int> SetKeyAsync(CommandLineArguments a, string name, AppLog log)
    {
        if (!Console.IsInputRedirected)
        {
            CliJson.Print(CliJson.Error(name,
                "Passare la chiave sullo standard input, ad esempio: Get-Content chiave.txt | .\\PuntaEAscolta.exe --set-key"));
            return 1;
        }

        string key = (Console.In.ReadLine() ?? "").Trim();
        if (key.Length == 0)
        {
            CliJson.Print(CliJson.Error(name, "Nessuna chiave ricevuta sullo standard input."));
            return 1;
        }
        if (key.Length > 256 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
        {
            CliJson.Print(CliJson.Error(name, "La chiave ricevuta non ha un formato valido (spazi, caratteri di controllo o troppo lunga)."));
            return 1;
        }

        var store = AppServices.OpenSettings(log);
        bool? verified = null;
        string? verifyNote = null;
        if (a.Verify)
        {
            using var http = AppServices.CreateHttpClient();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                verified = await new ElevenLabsAccountClient(http).ValidateKeyAsync(key, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SpeechProviderException or OperationCanceledException or HttpRequestException)
            {
                verifyNote = $"Verifica non possibile ({ex.Message}): la chiave viene salvata comunque.";
            }

            if (verified == false)
            {
                CliJson.Print(CliJson.Error(name, "ElevenLabs ha rifiutato la chiave: non è stata salvata."));
                return 1;
            }
        }

        var copy = JsonSettingsStore.Clone(store.Current);
        copy.Speech.ElevenLabsProtectedApiKey = new DpapiSecretProtector().Protect(key);
        store.Save(copy);
        log.Info("Chiave ElevenLabs aggiornata dalla riga di comando");

        bool running = SingleInstance.IsRunning();
        CliJson.Print(new
        {
            command = name,
            ok = true,
            saved = true,
            verified,
            note = verifyNote,
            settingsFile = store.FilePath,
            voiceConfigured = !string.IsNullOrWhiteSpace(copy.Speech.ElevenLabsVoiceId),
            message = "Chiave salvata, cifrata con DPAPI (vale solo per questo utente su questo PC)." +
                      (running ? " L'app è in esecuzione: chiuderla e riaprirla per usare la nuova chiave." : ""),
        });
        return 0;
    }
}
