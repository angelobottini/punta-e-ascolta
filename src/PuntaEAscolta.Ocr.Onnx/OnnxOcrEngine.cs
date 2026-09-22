using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using RapidOcrNet;
using SkiaSharp;
using OcrResult = PuntaEAscolta.Core.Abstractions.OcrResult;

namespace PuntaEAscolta.Ocr.Onnx;

/// <summary>
/// Motore OCR locale basato su PaddleOCR PP-OCRv5 (modelli "latin" inclusi nel pacchetto RapidOcrNet) eseguito con
/// ONNX Runtime su sola CPU. Pipeline a due stadi: rilevamento delle righe (DBNet) sull'intera immagine, poi riconoscimento
/// (CRNN) dei soli ritagli richiesti. Il classificatore di orientamento (0/180 gradi) non viene caricato: a schermo il
/// testo non è mai capovolto.
/// </summary>
/// <remarks>
/// - Inizializzazione pigra alla prima richiesta oppure anticipata con <see cref="WarmUpAsync"/>.
/// - Una sola richiesta alla volta (semaforo); il lavoro gira su un thread dedicato a priorità BelowNormal.
/// - Le coordinate restituite sono sempre quelle dell'immagine originale passata dal chiamante.
/// - Se le librerie native (onnxruntime.dll, libSkiaSharp.dll) o i modelli mancano, <see cref="IsAvailable"/> diventa
///   false e il motivo è in <see cref="UnavailableReason"/> e nel registro.
/// </remarks>
public sealed class OnnxOcrEngine : IOcrEngine
{
    public const string EngineName = "ONNX PP-OCRv5";

    private const string DetModelFile = "ch_PP-OCRv5_mobile_det.onnx";
    private const string RecModelFile = "latin_PP-OCRv5_rec_mobile_infer.onnx";
    private const string KeysFile = "ppocrv5_latin_dict.txt";

    private readonly ILog _log;
    private readonly string _modelsDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initLock = new();

    private TextDetector? _detector;
    private TextRecognizer? _recognizer;
    private volatile bool _initialized;
    private volatile string? _unavailableReason;
    private volatile bool _disposed;

    /// <param name="log">Registro diagnostico.</param>
    /// <param name="modelsDirectory">Cartella con i quattro file del set PP-OCRv5 latin. Predefinita: &lt;cartella dell'app&gt;\models\v5.</param>
    public OnnxOcrEngine(ILog log, string? modelsDirectory = null)
    {
        _log = log ?? NullLog.Instance;
        _modelsDirectory = string.IsNullOrWhiteSpace(modelsDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "models", "v5")
            : Path.GetFullPath(modelsDirectory);
    }

    public string Name => EngineName;

    /// <summary>
    /// False dopo un errore di inizializzazione (librerie native o modelli mancanti) o dopo Dispose. Prima della
    /// prima inizializzazione risponde in base alla presenza dei file dei modelli, senza caricare nulla.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (_disposed || _unavailableReason is not null)
            {
                return false;
            }

            return _initialized || ModelFilesExist();
        }
    }

    /// <summary>Motivo (in italiano) per cui il motore non è disponibile, oppure null.</summary>
    public string? UnavailableReason => _unavailableReason;

    /// <summary>Cartella dei modelli effettivamente usata.</summary>
    public string ModelsDirectory => _modelsDirectory;

    // ----- Parametri regolabili (valori iniziali dalla ricerca docs/research/ocr-onnx.md) -----

    /// <summary>Le righe con confidenza media del riconoscitore sotto questa soglia vengono scartate.</summary>
    public double MinLineConfidence { get; set; } = 0.5;

    /// <summary>Thread intra-op di ONNX Runtime (letto all'inizializzazione). Lascia core liberi per audio e voce.</summary>
    public int IntraOpThreads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    /// <summary>Arena di memoria CPU di ONNX Runtime. Con forme dinamiche l'arena può crescere: predefinito disattivata.</summary>
    public bool EnableMemoryArena { get; set; } = false;

    /// <summary>Lato corto a cui viene portata l'immagine per il rilevamento (come RapidOCR: limit_type=min, 736).</summary>
    public int DetectionShortSideTarget { get; set; } = 736;

    /// <summary>Ingrandimento massimo prima del rilevamento.</summary>
    public double MaxUpscale { get; set; } = 3.0;

    /// <summary>Lato lungo massimo dell'immagine data al rilevatore.</summary>
    public int MaxDetectionSide { get; set; } = 2000;

    /// <summary>
    /// Se l'altezza mediana delle righe trovate (in pixel originali) è sotto questo valore e il primo passaggio ha usato
    /// meno di 2x, il rilevamento viene ripetuto a 2x (testo piccolo delle interfacce).
    /// </summary>
    public double SmallTextHeightPx { get; set; } = 20;

    /// <summary>Raggio, in altezze di riga, entro cui <see cref="RecognizeNearAsync"/> riconosce i riquadri attorno al punto.</summary>
    public double NearDistanceInLineHeights { get; set; } = 3.0;

    /// <summary>Ritagli riconosciuti in parallelo (1 = in sequenza: i thread intra-op sono già occupati).</summary>
    public int RecognitionParallelism { get; set; } = 1;

    /// <summary>Soglie DBNet (valori RapidOCR).</summary>
    public float BoxScoreThresh { get; set; } = 0.5f;
    public float BoxThresh { get; set; } = 0.3f;
    public float UnClipRatio { get; set; } = 1.6f;

    /// <summary>Immagini più basse di così vengono allungate con bande sopra e sotto prima del rilevamento.</summary>
    public int LetterboxMinHeight { get; set; } = 32;

    /// <summary>Rapporto larghezza/altezza oltre il quale si aggiungono le bande.</summary>
    public double LetterboxMaxWidthHeightRatio { get; set; } = 8.0;

    /// <summary>Tempi dell'ultima richiesta, per diagnosi e bench.</summary>
    public OnnxOcrTimings LastTimings { get; private set; } = new(0, 0, 0, 0, 0, 1.0, false);

    // ----- API pubblica -----

    /// <summary>Carica le sessioni ONNX e fa un'inferenza di prova (rilevamento e riconoscimento) su un'immagine finta.</summary>
    public Task WarmUpAsync() => RunOnWorkerAsync(WarmUpCore, CancellationToken.None);

    /// <summary>Rileva e riconosce tutte le righe dell'immagine.</summary>
    public Task<OcrResult> RecognizeAsync(CapturedImage image, CancellationToken ct) => RecognizeCoreAsync(image, null, ct);

    /// <summary>
    /// Percorso rapido: rileva i riquadri su tutta l'immagine ma riconosce solo quelli entro
    /// <see cref="NearDistanceInLineHeights"/> altezze di riga dal punto (x, y) in coordinate immagine.
    /// </summary>
    public Task<OcrResult> RecognizeNearAsync(CapturedImage image, double x, double y, CancellationToken ct) =>
        RecognizeCoreAsync(image, (x, y), ct);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_initLock)
        {
            _detector?.Dispose();
            _recognizer?.Dispose();
            _detector = null;
            _recognizer = null;
        }

        _gate.Dispose();
    }

    // ----- Inizializzazione -----

    private bool ModelFilesExist() =>
        File.Exists(Path.Combine(_modelsDirectory, DetModelFile))
        && File.Exists(Path.Combine(_modelsDirectory, RecModelFile))
        && File.Exists(Path.Combine(_modelsDirectory, KeysFile));

    /// <summary>Carica le sessioni una volta sola. Restituisce false (e registra il motivo) se qualcosa manca.</summary>
    private bool EnsureInitialized()
    {
        if (_initialized)
        {
            return true;
        }

        lock (_initLock)
        {
            if (_initialized)
            {
                return true;
            }

            if (_disposed || _unavailableReason is not null)
            {
                return false;
            }

            var sw = Stopwatch.StartNew();
            TextDetector? detector = null;
            TextRecognizer? recognizer = null;
            try
            {
                string det = Path.Combine(_modelsDirectory, DetModelFile);
                string rec = Path.Combine(_modelsDirectory, RecModelFile);
                string keys = Path.Combine(_modelsDirectory, KeysFile);
                if (!ModelFilesExist())
                {
                    throw new FileNotFoundException($"Modelli OCR non trovati in '{_modelsDirectory}'.");
                }

                int threads = Math.Max(1, IntraOpThreads);
                using var options = CreateSessionOptions(threads);

                detector = new TextDetector();
                detector.InitModel(det, options);

                recognizer = new TextRecognizer();
                recognizer.InitModel(rec, keys, options);

                _detector = detector;
                _recognizer = recognizer;
                _initialized = true;
                _log.Info(string.Create(CultureInfo.InvariantCulture,
                    $"OCR ONNX pronto in {sw.ElapsedMilliseconds} ms: modelli in '{_modelsDirectory}', {threads} thread, arena {(EnableMemoryArena ? "attiva" : "disattivata")}."));
                return true;
            }
            catch (Exception ex)
            {
                detector?.Dispose();
                recognizer?.Dispose();
                var root = ex.GetBaseException();
                string reason = root switch
                {
                    DllNotFoundException => $"libreria nativa mancante ({root.Message})",
                    BadImageFormatException => $"libreria nativa dell'architettura sbagliata ({root.Message})",
                    EntryPointNotFoundException => $"libreria nativa incompatibile, forse onnxruntime.dll di sistema ({root.Message})",
                    FileNotFoundException => root.Message,
                    _ => $"{root.GetType().Name}: {root.Message}"
                };
                _unavailableReason = reason;
                _log.Error($"OCR ONNX non disponibile: {reason}", ex);
                return false;
            }
        }
    }

    private SessionOptions CreateSessionOptions(int threads)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            EnableCpuMemArena = EnableMemoryArena,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };

        // I thread intra-op di ORT altrimenti restano in attesa attiva dopo ogni inferenza: in un'app di sistema
        // sempre accesa è CPU sprecata. Il costo è qualche millisecondo di risveglio a ogni richiesta.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        return options;
    }

    // ----- Esecuzione sul thread di lavoro -----

    private Task RunOnWorkerAsync(Action work, CancellationToken ct) =>
        RunOnWorkerAsync(() => { work(); return true; }, ct);

    private async Task<T> RunOnWorkerAsync<T>(Func<T> work, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Factory.StartNew(() =>
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                }
                catch (Exception ex)
                {
                    _log.Debug($"OCR ONNX: impossibile abbassare la priorità del thread ({ex.Message}).");
                }

                return work();
            }, ct, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)
            .ConfigureAwait(false);
        }
        finally
        {
            if (!_disposed)
            {
                try { _gate.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task<OcrResult> RecognizeCoreAsync(CapturedImage image, (double X, double Y)? focus, CancellationToken ct)
    {
        if (_disposed)
        {
            return OcrResult.Empty(Name);
        }

        if (image is null || image.Width <= 0 || image.Height <= 0 || image.Bgra is null)
        {
            _log.Warn("OCR ONNX: immagine vuota o non valida.");
            return OcrResult.Empty(Name);
        }

        try
        {
            return await RunOnWorkerAsync(() => Pipeline(image, focus, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            return OcrResult.Empty(Name);
        }
        catch (Exception ex)
        {
            _log.Error("OCR ONNX: errore durante il riconoscimento.", ex);
            return OcrResult.Empty(Name);
        }
    }

    private void WarmUpCore()
    {
        if (!EnsureInitialized())
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            // Zona tipica (900x360) grigio scuro: forma realistica per il rilevatore.
            using var zone = new SKBitmap(new SKImageInfo(900, 360, SKColorType.Bgra8888, SKAlphaType.Opaque));
            zone.Erase(new SKColor(40, 40, 40));
            var scale = ComputeScale(zone.Width, zone.Height, out int dstW, out int dstH);
            _ = _detector!.GetTextBoxes(zone, new ScaleParam(zone.Width, zone.Height, dstW, dstH), BoxScoreThresh, BoxThresh, UnClipRatio);
            long detMs = sw.ElapsedMilliseconds;

            // Ritaglio di una riga (48 px di altezza è l'ingresso del riconoscitore).
            using var line = new SKBitmap(new SKImageInfo(240, 24, SKColorType.Bgra8888, SKAlphaType.Opaque));
            line.Erase(new SKColor(40, 40, 40));
            _ = _recognizer!.GetTextLine(line);
            _log.Info(string.Create(CultureInfo.InvariantCulture,
                $"OCR ONNX riscaldato in {sw.ElapsedMilliseconds} ms (rilevamento {detMs} ms a {scale:0.00}x, riconoscimento {sw.ElapsedMilliseconds - detMs} ms)."));
        }
        catch (Exception ex)
        {
            _log.Warn($"OCR ONNX: riscaldamento non riuscito ({ex.GetBaseException().Message}).");
        }
    }

    // ----- Pipeline -----

    private OcrResult Pipeline(CapturedImage image, (double X, double Y)? focus, CancellationToken ct)
    {
        if (!EnsureInitialized())
        {
            return OcrResult.Empty(Name);
        }

        var total = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        using var source = BitmapUtils.FromCapturedImage(image);
        var work = BitmapUtils.Letterbox(source, LetterboxMinHeight, LetterboxMaxWidthHeightRatio, out int letterboxTop);
        try
        {
            // 1. Rilevamento (eventualmente ripetuto a 2x per il testo piccolo).
            var detSw = Stopwatch.StartNew();
            double scale1 = ComputeScale(work.Width, work.Height, out int dstW, out int dstH);
            var boxes = Detect(work, dstW, dstH, ct);
            double usedScale = scale1;
            bool secondPass = false;

            double medianH = MedianHeight(boxes);
            bool tiny = boxes.Count > 0 && medianH < SmallTextHeightPx;
            if ((tiny || boxes.Count == 0) && scale1 < 2.0)
            {
                double cap = MaxDetectionSide / (double)Math.Max(work.Width, work.Height);
                double scale2 = Math.Min(2.0, cap);
                if (scale2 >= scale1 * 1.2)
                {
                    ct.ThrowIfCancellationRequested();
                    int w2 = RoundToMultiple32(work.Width * scale2), h2 = RoundToMultiple32(work.Height * scale2);
                    var boxes2 = Detect(work, w2, h2, ct);
                    if (boxes2.Count >= boxes.Count)
                    {
                        boxes = boxes2;
                        usedScale = scale2;
                        secondPass = true;
                        medianH = MedianHeight(boxes);
                    }
                }
            }

            long detMs = detSw.ElapsedMilliseconds;
            ct.ThrowIfCancellationRequested();

            // 2. Scelta dei riquadri da riconoscere.
            IReadOnlyList<TextBox> selected = boxes;
            if (focus is { } f && boxes.Count > 0)
            {
                double lineH = medianH > 0 ? medianH : 16;
                double maxDist = NearDistanceInLineHeights * lineH;
                double fy = f.Y + letterboxTop;
                selected = boxes.Where(b => DistanceToQuad(b.BoxPoints, f.X, fy) <= maxDist).ToList();
            }

            // 3. Riconoscimento dei ritagli.
            var recSw = Stopwatch.StartNew();
            var lines = Recognize(work, selected, letterboxTop, image.Width, image.Height, ct);
            long recMs = recSw.ElapsedMilliseconds;

            LastTimings = new OnnxOcrTimings((int)total.ElapsedMilliseconds, (int)detMs, (int)recMs, boxes.Count, selected.Count, usedScale, secondPass);

            if (_log.IsDebugEnabled)
            {
                _log.Debug(string.Create(CultureInfo.InvariantCulture,
                    $"OCR ONNX: {image.Width}x{image.Height}, scala {usedScale:0.00}x{(secondPass ? " (secondo passaggio)" : "")}, {boxes.Count} riquadri, {selected.Count} riconosciuti, {lines.Count} righe valide; rilevamento {detMs} ms, riconoscimento {recMs} ms, totale {total.ElapsedMilliseconds} ms."));
                foreach (var l in lines)
                {
                    _log.Debug(string.Create(CultureInfo.InvariantCulture,
                        $"  [{l.Box.X:0},{l.Box.Y:0} {l.Box.Width:0}x{l.Box.Height:0}] {l.Confidence:0.00} '{l.Text}'"));
                }
            }

            return new OcrResult(lines, Name, (int)total.ElapsedMilliseconds);
        }
        finally
        {
            if (!ReferenceEquals(work, source))
            {
                work.Dispose();
            }
        }
    }

    private IReadOnlyList<TextBox> Detect(SKBitmap work, int dstW, int dstH, CancellationToken ct)
    {
        var scale = new ScaleParam(work.Width, work.Height, dstW, dstH);
        var boxes = _detector!.GetTextBoxes(work, scale, BoxScoreThresh, BoxThresh, UnClipRatio, ct);
        if (boxes is null)
        {
            // RapidOcrNet inghiotte le eccezioni del rilevatore e restituisce null.
            _log.Warn(string.Create(CultureInfo.InvariantCulture, $"OCR ONNX: il rilevatore non ha risposto ({work.Width}x{work.Height} -> {dstW}x{dstH})."));
            return [];
        }

        // Scarta i riquadri degeneri che il ritaglio non potrebbe gestire.
        return boxes.Where(b => b.BoxPoints is { Length: 4 } && QuadHeight(b.BoxPoints) >= 2 && QuadWidth(b.BoxPoints) >= 2).ToList();
    }

    private List<OcrLine> Recognize(SKBitmap work, IReadOnlyList<TextBox> boxes, int letterboxTop, int originalWidth, int originalHeight, CancellationToken ct)
    {
        var result = new List<OcrLine>(boxes.Count);
        if (boxes.Count == 0)
        {
            return result;
        }

        var crops = new List<SKBitmap>(boxes.Count);
        var cropBoxes = new List<TextBox>(boxes.Count);
        try
        {
            foreach (var box in boxes)
            {
                ct.ThrowIfCancellationRequested();
                var crop = BitmapUtils.CropQuad(work, box.BoxPoints);
                if (crop is null)
                {
                    continue;
                }

                crops.Add(crop);
                cropBoxes.Add(box);
            }

            if (crops.Count == 0)
            {
                return result;
            }

            TextLine[] textLines = _recognizer!.GetTextLines(crops.ToArray(), Math.Max(1, RecognitionParallelism), null, ct);

            for (int i = 0; i < textLines.Length && i < cropBoxes.Count; i++)
            {
                var line = BuildLine(textLines[i], cropBoxes[i], letterboxTop, originalWidth, originalHeight);
                if (line is not null)
                {
                    result.Add(line);
                }
            }
        }
        finally
        {
            foreach (var c in crops)
            {
                c.Dispose();
            }
        }

        return result;
    }

    /// <summary>Converte una riga del riconoscitore in OcrLine (coordinate originali), oppure null se va scartata.</summary>
    private OcrLine? BuildLine(TextLine textLine, TextBox box, int letterboxTop, int originalWidth, int originalHeight)
    {
        if (textLine?.Chars is null || textLine.Chars.Length == 0)
        {
            return null;
        }

        int n = textLine.Chars.Length;
        float[]? scores = textLine.CharScores is { Length: var sl } && sl == n ? textLine.CharScores : null;
        int[]? cols = textLine.CharCols is { Length: var cl } && cl == n && textLine.ColCount > 0 ? textLine.CharCols : null;

        // Caratteri tenuti, con punteggio e posizione (frazione 0..1 lungo la riga).
        var kept = new List<(string Text, float Score, double Start, double End)>(n);
        for (int i = 0; i < n; i++)
        {
            string? mapped = LatinTextFilter.MapCharacter(textLine.Chars[i]);
            if (mapped is null)
            {
                continue;
            }

            double start, end;
            if (cols is not null)
            {
                start = cols[i] / (double)textLine.ColCount;
                end = (cols[i] + 1) / (double)textLine.ColCount;
            }
            else
            {
                start = i / (double)n;
                end = (i + 1) / (double)n;
            }

            kept.Add((mapped, scores?[i] ?? 1f, start, end));
        }

        if (kept.Count == 0)
        {
            return null;
        }

        double confidence = kept.Average(k => k.Score);
        string text = LatinTextFilter.CollapseSpaces(string.Concat(kept.Select(k => k.Text)));
        if (text.Length == 0)
        {
            return null;
        }

        if (confidence < MinLineConfidence)
        {
            if (_log.IsDebugEnabled)
            {
                _log.Debug(string.Create(CultureInfo.InvariantCulture, $"OCR ONNX: riga scartata per confidenza {confidence:0.00} < {MinLineConfidence:0.00}: '{text}'"));
            }

            return null;
        }

        // Riquadro della riga in coordinate originali (si toglie la banda del letterbox).
        var quad = new SKPointI[4];
        for (int i = 0; i < 4; i++)
        {
            quad[i] = new SKPointI(box.BoxPoints[i].X, box.BoxPoints[i].Y - letterboxTop);
        }

        var lineRect = ClampRect(BoundsOf(quad), originalWidth, originalHeight);
        var words = BuildWords(kept, quad, originalWidth, originalHeight);
        return new OcrLine(text, lineRect, words, confidence);
    }

    /// <summary>Spezza la riga in parole sugli spazi e stima il riquadro di ciascuna dalle colonne CTC del riconoscitore.</summary>
    private static IReadOnlyList<OcrWord> BuildWords(List<(string Text, float Score, double Start, double End)> kept, SKPointI[] quad, int w, int h)
    {
        var words = new List<OcrWord>();
        var current = new System.Text.StringBuilder();
        double wordStart = -1, wordEnd = -1;

        void Flush()
        {
            if (current.Length > 0 && wordStart >= 0)
            {
                words.Add(new OcrWord(current.ToString(), ClampRect(SubQuadBounds(quad, wordStart, wordEnd), w, h)));
            }

            current.Clear();
            wordStart = wordEnd = -1;
        }

        foreach (var k in kept)
        {
            foreach (char c in k.Text)
            {
                if (char.IsWhiteSpace(c))
                {
                    Flush();
                    continue;
                }

                if (current.Length == 0)
                {
                    wordStart = k.Start;
                }

                wordEnd = k.End;
                current.Append(c);
            }
        }

        Flush();
        return words;
    }

    // ----- Geometria -----

    private double ComputeScale(int width, int height, out int dstW, out int dstH)
    {
        double s = DetectionShortSideTarget / (double)Math.Max(1, Math.Min(width, height));
        s = Math.Clamp(s, 1.0, Math.Max(1.0, MaxUpscale));
        double cap = MaxDetectionSide / (double)Math.Max(width, height);
        s = Math.Min(s, cap);
        dstW = RoundToMultiple32(width * s);
        dstH = RoundToMultiple32(height * s);
        return s;
    }

    private static int RoundToMultiple32(double value) => Math.Max(32, (int)Math.Round(value / 32.0) * 32);

    private static double QuadWidth(SKPointI[] q) => BitmapUtils.Distance(q[0], q[1]);
    private static double QuadHeight(SKPointI[] q) => BitmapUtils.Distance(q[0], q[3]);

    private static double MedianHeight(IReadOnlyList<TextBox> boxes)
    {
        if (boxes.Count == 0)
        {
            return 0;
        }

        var heights = boxes.Select(b => QuadHeight(b.BoxPoints)).OrderBy(v => v).ToArray();
        int mid = heights.Length / 2;
        return heights.Length % 2 == 1 ? heights[mid] : (heights[mid - 1] + heights[mid]) / 2.0;
    }

    private static ImageRect BoundsOf(SKPointI[] q)
    {
        int minX = q.Min(p => p.X), maxX = q.Max(p => p.X);
        int minY = q.Min(p => p.Y), maxY = q.Max(p => p.Y);
        return new ImageRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Riquadro della porzione [start, end] (frazioni lungo il lato superiore) del quadrilatero.</summary>
    private static ImageRect SubQuadBounds(SKPointI[] q, double start, double end)
    {
        start = Math.Clamp(start, 0, 1);
        end = Math.Clamp(end, start, 1);
        var pts = new[]
        {
            Lerp(q[0], q[1], start), Lerp(q[0], q[1], end),
            Lerp(q[3], q[2], end), Lerp(q[3], q[2], start)
        };
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        return new ImageRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static SKPoint Lerp(SKPointI a, SKPointI b, double t) =>
        new((float)(a.X + (b.X - a.X) * t), (float)(a.Y + (b.Y - a.Y) * t));

    private static ImageRect ClampRect(ImageRect r, int w, int h)
    {
        double x = Math.Clamp(r.X, 0, w), y = Math.Clamp(r.Y, 0, h);
        double right = Math.Clamp(r.Right, x, w), bottom = Math.Clamp(r.Bottom, y, h);
        return new ImageRect(x, y, right - x, bottom - y);
    }

    /// <summary>Distanza dal punto al rettangolo che racchiude il quadrilatero (0 se dentro).</summary>
    private static double DistanceToQuad(SKPointI[] q, double px, double py)
    {
        var r = BoundsOf(q);
        double dx = Math.Max(Math.Max(r.X - px, 0), px - r.Right);
        double dy = Math.Max(Math.Max(r.Y - py, 0), py - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>Tempi e contatori dell'ultima richiesta OCR.</summary>
/// <param name="TotalMs">Tempo complessivo della pipeline.</param>
/// <param name="DetectMs">Rilevamento (uno o due passaggi).</param>
/// <param name="RecognizeMs">Ritaglio e riconoscimento.</param>
/// <param name="BoxesDetected">Riquadri trovati.</param>
/// <param name="BoxesRecognized">Riquadri passati al riconoscitore.</param>
/// <param name="DetectionScale">Fattore di ingrandimento usato per il rilevamento.</param>
/// <param name="SecondPass">True se il rilevamento è stato ripetuto a 2x.</param>
public sealed record OnnxOcrTimings(int TotalMs, int DetectMs, int RecognizeMs, int BoxesDetected, int BoxesRecognized, double DetectionScale, bool SecondPass);
