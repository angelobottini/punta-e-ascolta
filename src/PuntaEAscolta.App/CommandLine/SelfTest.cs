using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using PuntaEAscolta.App.Hosting;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Speech.ElevenLabs;
using PuntaEAscolta.Windows.Audio;
using PuntaEAscolta.Windows.Input;
using static PuntaEAscolta.App.Native.NativeMethods;

namespace PuntaEAscolta.App.CommandLine;

/// <summary>
/// Autodiagnosi silenziosa (nessun suono): processo e DPI, impostazioni, OCR (lingue, prova su un'immagine sintetica con
/// i due motori), voci (sintesi senza riproduzione), audio, hook del mouse per 1 s, accessibilità sotto il puntatore,
/// chiave ElevenLabs. Ogni sezione è protetta: un errore diventa un campo "error" e rende l'esito negativo.
/// </summary>
internal static class SelfTest
{
    private const string SampleText = "Punta e Ascolta 2026";

    public static async Task<int> RunAsync(string name, AppLog log)
    {
        var total = Stopwatch.StartNew();
        var problems = new List<string>();
        var warnings = new List<string>();
        using var services = AppServices.Create(log);
        var settings = services.Settings.Current;
        log.Info("Autodiagnosi avviata dalla riga di comando");

        object process = Section("processo", problems, () =>
        {
            nint context = GetThreadDpiAwarenessContext();
            bool pmv2 = AreDpiAwarenessContextsEqual(context, DpiContextPerMonitorV2);
            if (!pmv2) problems.Add("Il processo non è Per-Monitor V2: coordinate di mouse, accessibilità e cattura non coincidono.");
            return new
            {
                version = AppServices.Version,
                architecture = AppServices.Architecture,
                os = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                framework = RuntimeInformation.FrameworkDescription,
                directory = AppServices.AppDirectory,
                dpiAwareness = GetAwarenessFromDpiAwarenessContext(context),
                perMonitorV2 = pmv2,
            };
        });

        object settingsInfo = Section("impostazioni", problems, () => new
        {
            file = services.SettingsStore.FilePath,
            exists = File.Exists(services.SettingsStore.FilePath),
            portable = services.SettingsStore.IsPortable,
            dataDirectory = services.SettingsStore.DataDirectory,
            logDirectory = services.FileLog.DirectoryPath,
        });

        object ocr = await SectionAsync("ocr", problems, async () =>
        {
            var languages = global::Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
            if (languages.Length == 0) problems.Add("Nessuna lingua OCR di Windows installata.");
            var image = RenderSample();

            var windowsTest = await RecognizeSampleAsync(services.WindowsOcr, image).ConfigureAwait(false);
            if (!services.WindowsOcr.IsAvailable) problems.Add("OCR di Windows non disponibile.");
            else if (!windowsTest.Found) warnings.Add("L'OCR di Windows non ha letto il testo di prova.");

            var warm = Stopwatch.StartNew();
            await services.OnnxOcr.WarmUpAsync().ConfigureAwait(false);
            long warmMs = warm.ElapsedMilliseconds;
            object onnx;
            if (services.OnnxOcr.IsAvailable)
            {
                var onnxTest = await RecognizeSampleAsync(services.OnnxOcr, image).ConfigureAwait(false);
                if (!onnxTest.Found) warnings.Add("L'OCR ONNX non ha letto il testo di prova.");
                onnx = new { available = true, warmUpMs = warmMs, modelsDirectory = services.OnnxOcr.ModelsDirectory, test = onnxTest };
            }
            else
            {
                warnings.Add($"OCR ONNX non disponibile: {services.OnnxOcr.UnavailableReason}. Si usa solo l'OCR di Windows.");
                onnx = new { available = false, reason = services.OnnxOcr.UnavailableReason, modelsDirectory = services.OnnxOcr.ModelsDirectory };
            }

            return new
            {
                mode = settings.Ocr.Mode,
                configuredLanguage = settings.Ocr.WindowsOcrLanguage,
                windowsLanguages = languages,
                windows = new { available = services.WindowsOcr.IsAvailable, test = windowsTest },
                onnx,
            };
        }).ConfigureAwait(false);

        object voices = await SectionAsync("voci", problems, async () =>
        {
            var list = WindowsVoiceSynthesizer.GetVoices();
            if (list.Count == 0) problems.Add("Nessuna voce di Windows installata.");
            object synthesis;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var synth = await services.WindowsVoice
                    .SynthesizeAsync(new SpeechRequest("Prova", SpeechKind.System, "it"), cts.Token).ConfigureAwait(false);
                long bytes = await CountBytesAsync(synth.Pcm, cts.Token).ConfigureAwait(false);
                synthesis = new
                {
                    ok = bytes > 0,
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                    bytes,
                    sampleRate = synth.Format.SampleRate,
                    channels = synth.Format.Channels,
                    durationMs = synth.Format.BytesPerSecond > 0 ? bytes * 1000 / synth.Format.BytesPerSecond : 0,
                };
                if (bytes == 0) problems.Add("La voce di Windows non ha prodotto audio.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problems.Add($"Sintesi con la voce di Windows non riuscita: {ex.Message}");
                synthesis = new { ok = false, error = ex.Message };
            }

            return new
            {
                configured = settings.Speech.WindowsVoiceName,
                windows = list.Select(v => new { id = v.Id, name = v.DisplayName, language = v.Language, gender = v.Gender }).ToArray(),
                synthesis,
            };
        }).ConfigureAwait(false);

        object audio = await SectionAsync("audio", problems, async () =>
        {
            var warm = Stopwatch.StartNew();
            await services.Player.WarmUpAsync().ConfigureAwait(false);
            long warmMs = warm.ElapsedMilliseconds;
            var microphones = services.Recorder.GetDevices();
            string? configuredMic = string.IsNullOrWhiteSpace(settings.Dictation.MicrophoneDeviceId) ? null : settings.Dictation.MicrophoneDeviceId;
            if (configuredMic is not null && microphones.All(m => m.Id != configuredMic))
                warnings.Add("Il microfono scelto per la dettatura non è collegato: si userà quello predefinito.");
            return new
            {
                playerWarmUpMs = warmMs,
                engineWarm = services.Player.IsEngineWarm,
                microphones = microphones.Select(m => new { name = m.Name, isDefault = m.IsDefault }).ToArray(),
                dictationEnabled = settings.Dictation.Enabled,
            };
        }).ConfigureAwait(false);

        object hook = await SectionAsync("hook", problems, async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var input = new WindowsInputSource(log);
            IReadOnlyList<string> hookProblems;
            long startMs, disposeMs;
            try
            {
                input.Start(settings.Input, settings.Dictation);
                startMs = stopwatch.ElapsedMilliseconds;
                hookProblems = input.LastProblems.ToArray();
                await Task.Delay(1000).ConfigureAwait(false);
            }
            finally
            {
                stopwatch.Restart();
                input.Dispose();
                disposeMs = stopwatch.ElapsedMilliseconds;
            }

            bool threadOk = !hookProblems.Any(p => p.Contains("thread di input", StringComparison.OrdinalIgnoreCase));
            if (!threadOk) problems.Add("Hook del mouse non installato.");
            foreach (var p in hookProblems.Where(_ => threadOk)) warnings.Add(p);
            return new { installed = threadOk, startMs, activeMs = 1000, disposeMs, problems = hookProblems };
        }).ConfigureAwait(false);

        object uia = await SectionAsync("accessibilità", problems, async () =>
        {
            var point = NativePointer.GetPhysicalPosition();
            double dpi = services.Capture.GetDpiScale(point);
            var stopwatch = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var element = await services.Uia.GetElementAtAsync(point, cts.Token).ConfigureAwait(false);
            long ms = stopwatch.ElapsedMilliseconds;
            if (element is null) warnings.Add("Nessun elemento di accessibilità sotto il puntatore (può essere normale, es. desktop vuoto).");
            return new
            {
                pointer = CliJson.Point(point),
                dpiScale = dpi,
                elapsedMs = ms,
                element = element is null ? null : new
                {
                    kind = element.Kind,
                    name = element.Name,
                    value = element.IsPassword ? null : element.Value,
                    className = element.ClassName,
                    frameworkId = element.FrameworkId,
                    processName = element.ProcessName,
                    bounds = CliJson.Rect(element.Bounds),
                    parentKind = element.ParentKind,
                    textContext = element.Text is null ? null : new { pointerOverText = element.Text.PointerOverText, paragraphLength = element.Text.ParagraphText.Length },
                    platformMs = element.ElapsedMs,
                },
            };
        }).ConfigureAwait(false);

        object elevenLabs = await SectionAsync("elevenlabs", problems, async () =>
        {
            var (key, keyState) = CommandLineRunner.ReadApiKey(settings, services.Protector);
            object? check = null;
            if (key is not null)
            {
                try
                {
                    // Senza prova di lettura: l'autodiagnosi non spende crediti (la prova si fa con Verifica nelle impostazioni).
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var result = await new ElevenLabsAccountClient(services.Http).CheckKeyAsync(key, voiceId: null, cts.Token).ConfigureAwait(false);
                    check = new { valid = result.Valid, missingPermissions = result.MissingPermissions, message = result.Message };
                    if (!result.Valid) warnings.Add("La chiave ElevenLabs è stata rifiutata: si userà la voce di Windows.");
                    else if (result.MissingPermissions.Count > 0)
                        warnings.Add("Alla chiave ElevenLabs mancano dei permessi: " + string.Join(", ", result.MissingPermissions) +
                                     ". Per leggere serve almeno \"Text to Speech\" (provarlo con Verifica nelle impostazioni).");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    check = new { valid = (bool?)null, error = ex.Message };
                    warnings.Add($"Verifica della chiave ElevenLabs non possibile: {ex.Message}");
                }
            }
            else if (keyState != "assente")
            {
                warnings.Add("La chiave ElevenLabs salvata non è decifrabile su questo PC: va reinserita.");
            }

            return new
            {
                provider = settings.Speech.Provider,
                keyState,
                voiceId = settings.Speech.ElevenLabsVoiceId,
                voiceName = settings.Speech.ElevenLabsVoiceName,
                configured = services.ElevenLabs.IsConfigured,
                check,
                cache = new
                {
                    enabled = settings.Speech.CacheEnabled,
                    directory = services.Cache.Directory,
                    sizeBytes = services.Cache.GetSizeBytes(),
                    maxMegabytes = settings.Speech.CacheMaxMegabytes,
                },
            };
        }).ConfigureAwait(false);

        bool ok = problems.Count == 0;
        CliJson.Print(new
        {
            command = name,
            ok,
            elapsedMs = total.ElapsedMilliseconds,
            problems,
            warnings,
            process,
            settings = settingsInfo,
            ocr,
            voices,
            audio,
            hook,
            uia,
            elevenLabs,
        });
        log.Info($"Autodiagnosi terminata: {(ok ? "tutto a posto" : problems.Count + " problemi")}, {warnings.Count} avvisi");
        return ok ? 0 : 1;
    }

    private static object Section(string title, List<string> problems, Func<object> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex)
        {
            problems.Add($"Sezione {title}: {ex.GetType().Name}: {ex.Message}");
            return new { error = ex.Message };
        }
    }

    private static async Task<object> SectionAsync(string title, List<string> problems, Func<Task<object>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            problems.Add($"Sezione {title}: {ex.GetType().Name}: {ex.Message}");
            return new { error = ex.Message };
        }
    }

    private sealed record SampleResult(bool Found, string Text, int Lines, long ElapsedMs);

    private static async Task<SampleResult> RecognizeSampleAsync(IOcrEngine engine, CapturedImage image)
    {
        var stopwatch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await engine.RecognizeAsync(image, cts.Token).ConfigureAwait(false);
        string text = string.Join(" | ", result.Lines.Select(l => l.Text));
        bool found = text.Contains("Ascolta", StringComparison.OrdinalIgnoreCase);
        return new SampleResult(found, text, result.Lines.Count, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Testo nero su bianco, 20 px, come un'etichetta d'interfaccia al 125%.</summary>
    private static CapturedImage RenderSample()
    {
        const int width = 420, height = 80;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var font = new Font("Segoe UI", 20f, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString(SampleText, font, Brushes.Black, 20f, 24f);
        }

        var bgra = new byte[width * height * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * width * 4, width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return new CapturedImage(bgra, width, height, new ScreenRect(0, 0, width, height), 1.25);
    }

    private static async Task<long> CountBytesAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        long total = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) total += n;
        return total;
    }
}
