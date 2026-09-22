using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Ocr.Onnx;
using SkiaSharp;

namespace PuntaEAscolta.OcrBench;

/// <summary>
/// Banco di prova del motore OCR ONNX.
/// <code>
/// OcrBench &lt;immagine.png&gt; [x y] [--giri N] [--attese file.txt] [--scala f] [--thread N] [--confmin f]
///          [--senza-riscaldamento] [--ogni-riga] [--debug] [--silenzioso]
/// OcrBench --genera &lt;cartella&gt;
/// </code>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            if (args.Length >= 2 && args[0] == "--genera")
            {
                SceneGenerator.Generate(args[1]);
                return 0;
            }

            if (args.Length >= 2 && args[0] == "--robustezza")
            {
                return await Robustness.RunAsync(args[1]).ConfigureAwait(false);
            }

            var options = BenchOptions.Parse(args);
            if (options is null)
            {
                PrintUsage();
                return 2;
            }

            return await RunAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERRORE: {ex}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Uso: OcrBench <immagine.png> [x y] [--giri N] [--attese file.txt] [--scala f] [--thread N] [--confmin f]");
        Console.WriteLine("                [--senza-riscaldamento] [--ogni-riga] [--debug] [--silenzioso]");
        Console.WriteLine("                [--inverti] [--latocorto N] [--maxscala f] [--piccolo px] [--allunga f]");
        Console.WriteLine("                [--finestra LxA (al 100%)] [--scalavicino f] [--parallelo N] [--maxpixel N] [--solo-vicino]");
        Console.WriteLine("                [--zona X,Y]   (ritaglia 1125x450 attorno a X,Y come l'app al 125% e usa il centro come puntatore)");
        Console.WriteLine("     OcrBench --genera <cartella>   (crea le immagini di scena sintetiche)");
        Console.WriteLine("     OcrBench --robustezza <immagine.png>   (annullamento, buffer errati, concorrenza, Dispose)");
    }

    private static async Task<int> RunAsync(BenchOptions o)
    {
        var total = Stopwatch.StartNew();
        Console.WriteLine($"Processo {RuntimeInformation.ProcessArchitecture}, SO {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}, {Environment.ProcessorCount} processori logici");
        Console.WriteLine($"Memoria all'avvio: {MemoryText()}");

        var sw = Stopwatch.StartNew();
        var image = ImageLoader.Load(o.ImagePath, o.Scale, o.Invert);
        Console.WriteLine($"Immagine: {Path.GetFileName(o.ImagePath)} {image.Width}x{image.Height} (scala {o.Scale:0.###}), caricata in {sw.ElapsedMilliseconds} ms");
        if (o.Zone is { } zc)
        {
            // Come l'app: zona 900x360 al 100% moltiplicata per la scala 1,25, centrata sul puntatore e contenuta nell'immagine.
            int zw = Math.Min(image.Width, 1125), zh = Math.Min(image.Height, 450);
            int left = Math.Clamp(zc.X - zw / 2, 0, image.Width - zw), top = Math.Clamp(zc.Y - zh / 2, 0, image.Height - zh);
            var bgra = new byte[zw * zh * 4];
            for (int row = 0; row < zh; row++)
            {
                Array.Copy(image.Bgra, ((top + row) * image.Width + left) * 4, bgra, row * zw * 4, zw * 4);
            }

            image = new CapturedImage(bgra, zw, zh, new ScreenRect(left, top, zw, zh), 1.25);
            o = o.WithPoint(zc.X - left, zc.Y - top);
            Console.WriteLine($"Zona come l'app: {zw}x{zh} da ({left},{top}), puntatore nella zona ({zc.X - left},{zc.Y - top})");
        }

        var log = new ConsoleLog(o.Debug);
        using var engine = new OnnxOcrEngine(log);
        if (o.Threads > 0)
        {
            engine.IntraOpThreads = o.Threads;
        }

        if (o.MinConfidence >= 0)
        {
            engine.MinLineConfidence = o.MinConfidence;
        }

        if (o.ShortSide > 0)
        {
            engine.DetectionShortSideTarget = o.ShortSide;
        }

        if (o.MaxUpscale > 0)
        {
            engine.MaxUpscale = o.MaxUpscale;
        }

        if (o.SmallText >= 0)
        {
            engine.SmallTextHeightPx = o.SmallText;
        }

        if (o.Stretch > 0)
        {
            engine.RecognitionHorizontalStretch = o.Stretch;
        }

        if (o.WindowWidth > 0)
        {
            engine.NearWindowWidth = o.WindowWidth;
            engine.NearWindowHeight = o.WindowHeight;
        }

        if (o.NearScale > 0)
        {
            engine.NearDetectionScale = o.NearScale;
        }

        if (o.RecParallel > 0)
        {
            engine.RecognitionParallelism = o.RecParallel;
        }

        if (o.MaxPixels > 0)
        {
            engine.MaxDetectionPixels = o.MaxPixels;
        }

        Console.WriteLine($"Modelli: {engine.ModelsDirectory}; disponibile prima del caricamento: {engine.IsAvailable}; thread intra-op {engine.IntraOpThreads}");

        if (!o.SkipWarmUp)
        {
            sw.Restart();
            await engine.WarmUpAsync().ConfigureAwait(false);
            Console.WriteLine($"Caricamento + riscaldamento: {sw.ElapsedMilliseconds} ms; disponibile: {engine.IsAvailable}{(engine.UnavailableReason is { } r ? " (" + r + ")" : "")}");
            Console.WriteLine($"Memoria dopo il riscaldamento: {MemoryText()}");
        }

        PrintNativeModules();

        // Primo riconoscimento completo ("a freddo": prima immagine reale, forme nuove per ONNX Runtime).
        if (o.NearOnly && o.Point is { } only)
        {
            sw.Restart();
            var first = await engine.RecognizeNearAsync(image, only.X, only.Y, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"Solo percorso mirato, primo: {sw.ElapsedMilliseconds} ms ({TimingsText(engine.LastTimings)}), {first.Lines.Count} righe");
            var times = new List<long>();
            for (int i = 0; i < Math.Max(1, o.Runs); i++)
            {
                sw.Restart();
                first = await engine.RecognizeNearAsync(image, only.X, only.Y, CancellationToken.None).ConfigureAwait(false);
                times.Add(sw.ElapsedMilliseconds);
            }

            Console.WriteLine($"Solo percorso mirato, a caldo: mediana {Median(times)} ms; {TimingsText(engine.LastTimings)}");
            PrintLines(first);
            Console.WriteLine($"Memoria finale: {MemoryText()}");
            return 0;
        }

        sw.Restart();
        var cold = await engine.RecognizeAsync(image, CancellationToken.None).ConfigureAwait(false);
        long coldMs = sw.ElapsedMilliseconds;
        var coldTimings = engine.LastTimings;
        Console.WriteLine();
        Console.WriteLine($"RecognizeAsync a freddo: {coldMs} ms ({TimingsText(coldTimings)}), {cold.Lines.Count} righe");
        if (!o.Quiet)
        {
            PrintLines(cold);
        }

        // Giri a caldo.
        var warmMs = new List<long>();
        OcrResult last = cold;
        for (int i = 0; i < o.Runs; i++)
        {
            sw.Restart();
            last = await engine.RecognizeAsync(image, CancellationToken.None).ConfigureAwait(false);
            warmMs.Add(sw.ElapsedMilliseconds);
        }

        if (warmMs.Count > 0)
        {
            Console.WriteLine($"RecognizeAsync a caldo ({warmMs.Count} giri): mediana {Median(warmMs)} ms, min {warmMs.Min()} ms, max {warmMs.Max()} ms; ultimo: {TimingsText(engine.LastTimings)}");
            if (last.Lines.Count != cold.Lines.Count)
            {
                Console.WriteLine($"  ATTENZIONE: righe diverse tra freddo ({cold.Lines.Count}) e caldo ({last.Lines.Count}).");
            }
        }

        Console.WriteLine($"Memoria dopo il riconoscimento: {MemoryText()}");

        if (o.ExpectedPath is not null)
        {
            ExpectationReport.Print(o.ExpectedPath, cold);
        }

        // Percorso mirato attorno al punto.
        if (o.Point is { } p)
        {
            IPointOcrEngine pointEngine = engine;
            var nearMs = new List<long>();
            OcrResult? near = null;
            for (int i = 0; i < Math.Max(1, o.Runs); i++)
            {
                sw.Restart();
                near = await pointEngine.RecognizeAroundPointAsync(image, p.X, p.Y, CancellationToken.None).ConfigureAwait(false);
                nearMs.Add(sw.ElapsedMilliseconds);
            }

            Console.WriteLine();
            Console.WriteLine($"RecognizeNearAsync ({p.X:0},{p.Y:0}): mediana {Median(nearMs)} ms (primo {nearMs[0]} ms), {TimingsText(engine.LastTimings)}, {near!.Lines.Count} righe");
            PrintLines(near);
            var hit = near.Lines.FirstOrDefault(l => l.Box.Contains(p.X, p.Y))
                      ?? near.Lines.OrderBy(l => Math.Abs(l.Box.CenterY - p.Y)).FirstOrDefault();
            Console.WriteLine($"  Riga sotto il punto: {(hit is null ? "(nessuna)" : "'" + hit.Text + "'")}");
        }

        // Percorso mirato su ogni riga trovata: verifica che dia la stessa riga e misura il tempo.
        if (o.NearEachLine && cold.Lines.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("RecognizeNearAsync al centro di ogni riga del passaggio completo:");
            int same = 0;
            var times = new List<long>();
            foreach (var line in cold.Lines)
            {
                sw.Restart();
                var r = await engine.RecognizeNearAsync(image, line.Box.CenterX, line.Box.CenterY, CancellationToken.None).ConfigureAwait(false);
                times.Add(sw.ElapsedMilliseconds);
                var hit = r.Lines.FirstOrDefault(l => l.Box.Contains(line.Box.CenterX, line.Box.CenterY));
                bool ok = hit is not null && hit.Text == line.Text;
                if (ok)
                {
                    same++;
                }
                else
                {
                    Console.WriteLine($"  diversa: '{line.Text}' -> '{hit?.Text ?? "(nessuna)"}'");
                }
            }

            Console.WriteLine($"  {same}/{cold.Lines.Count} uguali; tempo mediana {Median(times)} ms, min {times.Min()} ms, max {times.Max()} ms; ultimo {TimingsText(engine.LastTimings)}");
        }

        Console.WriteLine();
        Console.WriteLine($"Memoria finale: {MemoryText()}; tempo complessivo {total.ElapsedMilliseconds} ms");
        return 0;
    }

    private static void PrintLines(OcrResult result)
    {
        foreach (var l in result.Lines)
        {
            var b = l.Box;
            Console.WriteLine($"  [{b.X,5:0},{b.Y,5:0} {b.Width,4:0}x{b.Height,-3:0}] {l.Confidence ?? double.NaN:0.000}  {l.Text}");
        }
    }

    private static string TimingsText(OnnxOcrTimings t) =>
        $"rilevamento {t.DetectMs} ms a {t.DetectionScale:0.00}x{(t.SecondPass ? " (2 passaggi)" : "")}{(t.Widened ? " (finestra allargata)" : "")}, riconoscimento {t.RecognizeMs} ms, riquadri {t.BoxesRecognized}/{t.BoxesDetected}";

    private static long Median(List<long> values)
    {
        var s = values.OrderBy(v => v).ToArray();
        return s.Length == 0 ? 0 : s[s.Length / 2];
    }

    private static string MemoryText()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return $"working set {p.WorkingSet64 / 1048576.0:0.0} MB (picco {p.PeakWorkingSet64 / 1048576.0:0.0} MB), privata {p.PrivateMemorySize64 / 1048576.0:0.0} MB, heap gestito {GC.GetTotalMemory(false) / 1048576.0:0.0} MB";
    }

    private static void PrintNativeModules()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            foreach (ProcessModule m in p.Modules)
            {
                string name = m.ModuleName ?? string.Empty;
                if (name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("SkiaSharp", StringComparison.OrdinalIgnoreCase))
                {
                    long size = File.Exists(m.FileName) ? new FileInfo(m.FileName).Length : 0;
                    Console.WriteLine($"Libreria nativa caricata: {m.FileName} ({size / 1048576.0:0.0} MB)");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Elenco dei moduli non disponibile: {ex.Message}");
        }
    }
}

/// <summary>Opzioni della riga di comando.</summary>
internal sealed class BenchOptions
{
    public required string ImagePath { get; init; }
    public (double X, double Y)? Point { get; init; }
    public int Runs { get; init; } = 5;
    public string? ExpectedPath { get; init; }
    public double Scale { get; init; } = 1.0;
    public int Threads { get; init; }
    public double MinConfidence { get; init; } = -1;
    public bool SkipWarmUp { get; init; }
    public bool NearEachLine { get; init; }
    public bool Debug { get; init; }
    public bool Quiet { get; init; }
    public bool Invert { get; init; }
    public int ShortSide { get; init; }
    public double MaxUpscale { get; init; }
    public double SmallText { get; init; } = -1;
    public double Stretch { get; init; }
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }
    public double NearScale { get; init; }
    public int RecParallel { get; init; }
    public int MaxPixels { get; init; }
    public bool NearOnly { get; init; }
    public (int X, int Y)? Zone { get; init; }

    public BenchOptions WithPoint(double x, double y) => new()
    {
        ImagePath = ImagePath, Point = (x, y), Runs = Runs, ExpectedPath = ExpectedPath, Scale = Scale, Threads = Threads,
        MinConfidence = MinConfidence, SkipWarmUp = SkipWarmUp, NearEachLine = NearEachLine, Debug = Debug, Quiet = Quiet,
        Invert = Invert, ShortSide = ShortSide, MaxUpscale = MaxUpscale, SmallText = SmallText, Stretch = Stretch,
        WindowWidth = WindowWidth, WindowHeight = WindowHeight, NearScale = NearScale, RecParallel = RecParallel,
        MaxPixels = MaxPixels, NearOnly = NearOnly, Zone = null
    };

    public static BenchOptions? Parse(string[] args)
    {
        var positional = new List<string>();
        int runs = 5, threads = 0;
        double scale = 1.0, minConf = -1;
        string? expected = null;
        bool skipWarm = false, nearEach = false, debug = false, quiet = false, invert = false;
        int shortSide = 0;
        double maxUpscale = 0, smallText = -1, stretch = 0, nearScale = 0;
        int windowW = 0, windowH = 0, recParallel = 0, maxPixels = 0;
        bool nearOnly = false;
        (int, int)? zone = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Manca il valore di {a}.");
            switch (a)
            {
                case "--giri": runs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--attese": expected = Next(); break;
                case "--scala": scale = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--thread": threads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--confmin": minConf = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--senza-riscaldamento": skipWarm = true; break;
                case "--ogni-riga": nearEach = true; break;
                case "--debug": debug = true; break;
                case "--silenzioso": quiet = true; break;
                case "--inverti": invert = true; break;
                case "--allunga": stretch = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--scalavicino": nearScale = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--parallelo": recParallel = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--maxpixel": maxPixels = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--solo-vicino": nearOnly = true; break;
                case "--zona":
                    var z = Next().Split(',');
                    zone = (int.Parse(z[0], CultureInfo.InvariantCulture), int.Parse(z[1], CultureInfo.InvariantCulture));
                    break;
                case "--finestra":
                    var parts = Next().Split('x');
                    windowW = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    windowH = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "--latocorto": shortSide = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--maxscala": maxUpscale = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--piccolo": smallText = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Opzione sconosciuta: {a}");
                    }

                    positional.Add(a);
                    break;
            }
        }

        if (positional.Count is not (1 or 3))
        {
            return null;
        }

        (double, double)? point = null;
        if (positional.Count == 3)
        {
            point = (double.Parse(positional[1], CultureInfo.InvariantCulture) * scale,
                     double.Parse(positional[2], CultureInfo.InvariantCulture) * scale);
        }

        return new BenchOptions
        {
            ImagePath = positional[0],
            Point = point,
            Runs = Math.Max(0, runs),
            ExpectedPath = expected,
            Scale = scale,
            Threads = threads,
            MinConfidence = minConf,
            SkipWarmUp = skipWarm,
            NearEachLine = nearEach,
            Debug = debug,
            Quiet = quiet,
            Invert = invert,
            ShortSide = shortSide,
            MaxUpscale = maxUpscale,
            SmallText = smallText,
            Stretch = stretch,
            WindowWidth = windowW,
            WindowHeight = windowH,
            NearScale = nearScale,
            RecParallel = recParallel,
            MaxPixels = maxPixels,
            NearOnly = nearOnly,
            Zone = zone
        };
    }
}

/// <summary>Registro su console per il banco di prova.</summary>
internal sealed class ConsoleLog(bool debug) : ILog
{
    private static readonly object Sync = new();

    public bool IsDebugEnabled => debug;

    public void Debug(string message)
    {
        if (debug)
        {
            Write("DEBUG", message);
        }
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("AVVISO", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERRORE", exception is null ? message : $"{message} {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            Console.WriteLine($"  [{level}] {message}");
        }
    }
}

/// <summary>Carica un PNG in una CapturedImage BGRA (come la cattura dello schermo), con ridimensionamento facoltativo.</summary>
internal static class ImageLoader
{
    public static CapturedImage Load(string path, double scale, bool invert)
    {
        using var decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException($"Immagine non leggibile: {path}");
        int w = Math.Max(1, (int)Math.Round(decoded.Width * scale));
        int h = Math.Max(1, (int)Math.Round(decoded.Height * scale));

        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var image = SKImage.FromBitmap(decoded))
        {
            // Le catture dello schermo sono opache: la trasparenza eventuale si compone su bianco.
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        var bgra = new byte[w * h * 4];
        var src = bitmap.GetPixelSpan();
        for (int y = 0; y < h; y++)
        {
            src.Slice(y * bitmap.RowBytes, w * 4).CopyTo(bgra.AsSpan(y * w * 4, w * 4));
        }

        if (invert)
        {
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = (byte)(255 - bgra[i]);
                bgra[i + 1] = (byte)(255 - bgra[i + 1]);
                bgra[i + 2] = (byte)(255 - bgra[i + 2]);
            }
        }

        return new CapturedImage(bgra, w, h, new ScreenRect(0, 0, w, h), 1.25);
    }
}

/// <summary>Confronta le righe riconosciute con un elenco di etichette attese (una per riga, UTF-8).</summary>
internal static class ExpectationReport
{
    public static void Print(string expectedPath, OcrResult result)
    {
        var expected = File.ReadAllLines(expectedPath, Encoding.UTF8)
            .Select(Normalize)
            .Where(s => s.Length > 0 && !s.StartsWith('#'))
            .ToList();
        var got = result.Lines.Select(l => Normalize(l.Text)).ToList();

        // "Per la voce": puntini finali ignorati (LabelCleaner li toglie comunque) e maiuscole ignorate.
        int exact = 0, speech = 0, prefix = 0;
        var missing = new List<string>();
        var gotSpeech = got.Select(ForSpeech).ToList();
        foreach (var e in expected)
        {
            string es = ForSpeech(e);
            if (got.Contains(e, StringComparer.Ordinal))
            {
                exact++;
            }
            else if (gotSpeech.Contains(es, StringComparer.OrdinalIgnoreCase))
            {
                speech++;
            }
            else if (gotSpeech.Any(g => g.StartsWith(es + " ", StringComparison.OrdinalIgnoreCase)))
            {
                prefix++;
            }
            else
            {
                var closest = got.OrderBy(g => Levenshtein(g.ToUpperInvariant(), e.ToUpperInvariant())).FirstOrDefault();
                missing.Add($"'{e}' (piu vicina: '{closest}')");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Attese {expected.Count}: esatte {exact}, uguali per la voce (puntini finali e maiuscole) {speech}, con testo in piu a destra {prefix}, sbagliate o mancanti {missing.Count} -> corrette per la voce {exact + speech + prefix}/{expected.Count}");
        foreach (var m in missing)
        {
            Console.WriteLine($"  sbagliata: {m}");
        }

        var expectedSpeech = expected.Select(ForSpeech).ToList();
        var extra = got.Where(g => !expectedSpeech.Any(e => ForSpeech(g).Equals(e, StringComparison.OrdinalIgnoreCase) || ForSpeech(g).StartsWith(e + " ", StringComparison.OrdinalIgnoreCase))).ToList();
        if (extra.Count > 0)
        {
            Console.WriteLine($"  righe non attese ({extra.Count}): {string.Join(" | ", extra)}");
        }
    }

    private static string ForSpeech(string s) => s.TrimEnd('.', ' ');

    /// <summary>Spazi normalizzati; i puntini di sospensione tipografici diventano tre punti.</summary>
    private static string Normalize(string s)
    {
        string ellipsis = ((char)0x2026).ToString();
        var t = s.Replace(ellipsis, "...", StringComparison.Ordinal).Trim();
        return string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
