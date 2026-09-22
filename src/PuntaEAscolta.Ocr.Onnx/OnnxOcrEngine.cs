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
/// ONNX Runtime su sola CPU. Pipeline a due stadi: rilevamento delle righe (DBNet), poi riconoscimento (SVTR/CTC) dei
/// ritagli. Il classificatore di orientamento (0/180 gradi) non viene caricato: a schermo il testo non è mai capovolto.
/// <see cref="RecognizeAsync"/> rileva su tutta l'immagine; <see cref="RecognizeNearAsync"/> rileva solo in una finestra
/// attorno al punto e riconosce solo le righe vicine (percorso consigliato: 3-5 volte più veloce).
/// </summary>
/// <remarks>
/// - Inizializzazione pigra alla prima richiesta oppure anticipata con <see cref="WarmUpAsync"/>.
/// - Una sola richiesta alla volta (semaforo); il lavoro gira su un thread dedicato a priorità BelowNormal.
/// - Le coordinate restituite sono sempre quelle dell'immagine originale passata dal chiamante.
/// - Se le librerie native (onnxruntime.dll, libSkiaSharp.dll) o i modelli mancano, <see cref="IsAvailable"/> diventa
///   false e il motivo è in <see cref="UnavailableReason"/> e nel registro.
/// </remarks>
public sealed class OnnxOcrEngine : IOcrEngine, IPointOcrEngine
{
    public const string EngineName = "ONNX PP-OCRv5";

    private const string DetModelFile = "ch_PP-OCRv5_mobile_det.onnx";
    private const string RecModelFile = "latin_PP-OCRv5_rec_mobile_infer.onnx";
    private const string KeysFile = "ppocrv5_latin_dict.txt";

    private readonly ILog _log;
    private readonly string _modelsDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
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
    public double MinLineConfidence { get; set; } = 0.6;

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
    /// Pixel massimi dell'immagine data al rilevatore (0 = nessun limite). Il costo del rilevamento è proporzionale ai pixel:
    /// sullo Snapdragon X circa 0,45 ms ogni 1000 pixel con 4 thread, post-elaborazione compresa.
    /// </summary>
    public int MaxDetectionPixels { get; set; } = 800_000;

    /// <summary>
    /// Se l'altezza mediana dei riquadri trovati (in pixel originali) è sotto questo valore, oppure non si è trovato nulla,
    /// e il primo passaggio ha usato meno di 2x, il rilevamento viene ripetuto a 2x (testo minuscolo). Al 125% le voci di
    /// menu di Affinity danno riquadri di 17-24 px e sono già lette bene a 1,5x: il secondo passaggio raddoppierebbe il tempo.
    /// </summary>
    public double SmallTextHeightPx { get; set; } = 14;

    /// <summary>
    /// Finestra del percorso mirato (<see cref="RecognizeNearAsync"/>), in pixel al 100% di scala: viene moltiplicata per
    /// <see cref="CapturedImage.DpiScale"/>. Il rilevamento avviene solo in questa finestra centrata sul punto; se la riga
    /// puntata ne tocca un bordo la finestra viene allargata.
    /// </summary>
    public int NearWindowWidth { get; set; } = 512;

    /// <inheritdoc cref="NearWindowWidth"/>
    public int NearWindowHeight { get; set; } = 160;

    /// <summary>Ingrandimento del rilevamento nel percorso mirato (testo piccolo delle interfacce).</summary>
    public double NearDetectionScale { get; set; } = 1.5;

    /// <summary>Raggio, in altezze di riga, entro cui <see cref="RecognizeNearAsync"/> riconosce i riquadri attorno al punto.</summary>
    public double NearDistanceInLineHeights { get; set; } = 1.5;

    /// <summary>
    /// Due righe riconosciute sulla stessa fascia orizzontale vengono unite in una sola se lo spazio fra i loro riquadri
    /// di inchiostro è minore di questo valore in altezze di riga. Il rilevatore separa spesso in parole il testo grande
    /// (cartelli); le schede affiancate delle interfacce distano circa 0,9 e l'etichetta e la scorciatoia di un menu
    /// molto di più, quindi restano separate.
    /// </summary>
    public double WordMergeGapInLineHeights { get; set; } = 0.6;

    /// <summary>Numero massimo di righe riconosciute nel percorso mirato.</summary>
    public int NearMaxLines { get; set; } = 6;

    /// <summary>
    /// Spazio verticale massimo fra i riquadri del rilevatore (in altezze di riquadro) perché due righe siano dello stesso
    /// blocco nel percorso mirato. I riquadri del rilevatore sono più alti del testo: le voci di menu distano circa 0,4.
    /// </summary>
    public double BlockGapInLineHeights { get; set; } = 0.2;

    /// <summary>
    /// Allungamento orizzontale dei ritagli prima del riconoscimento (1 = nessuno). Il riconoscitore CTC emette un simbolo
    /// ogni 8 pixel dell'ingresso alto 48: con i caratteri stretti delle interfacce le doppie ("cc", "tt") si fondono.
    /// </summary>
    public double RecognitionHorizontalStretch { get; set; } = 1.6;

    /// <summary>
    /// Ritagli riconosciuti in parallelo. Con 2 il riconoscimento di molte righe è circa un terzo più veloce (misurato con
    /// 4 thread intra-op sullo Snapdragon X); 1 = in sequenza, meno carico sulla CPU.
    /// </summary>
    public int RecognitionParallelism { get; set; } = 2;

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

    /// <summary>
    /// Carica le sessioni ONNX e fa un'inferenza di prova (rilevamento e riconoscimento) su un'immagine finta, così la
    /// prima lettura non paga il caricamento (circa 250 ms) e la prima inferenza (circa 200 ms). Non genera eccezioni:
    /// gli errori finiscono nel registro e in <see cref="UnavailableReason"/>.
    /// </summary>
    public Task WarmUpAsync() =>
        _disposed ? Task.CompletedTask : RunOnWorkerAsync(_ => { WarmUpCore(); return true; }, false, CancellationToken.None);

    /// <summary>Rileva e riconosce tutte le righe dell'immagine.</summary>
    public Task<OcrResult> RecognizeAsync(CapturedImage image, CancellationToken ct) => RecognizeCoreAsync(image, null, ct);

    /// <summary>
    /// Percorso rapido: rileva i riquadri solo in una finestra di <see cref="NearWindowWidth"/> x
    /// <see cref="NearWindowHeight"/> (per la scala del monitor) centrata sul punto (x, y) in coordinate immagine, allargata
    /// se taglia la riga puntata, e riconosce solo le righe sulla fascia del punto (o entro
    /// <see cref="NearDistanceInLineHeights"/> altezze di riga) più quelle attaccate dello stesso blocco.
    /// Le righe tagliate dalla finestra vengono scartate.
    /// </summary>
    public Task<OcrResult> RecognizeNearAsync(CapturedImage image, double x, double y, CancellationToken ct) =>
        RecognizeCoreAsync(image, (x, y), ct);

    /// <summary>Implementazione di <see cref="IPointOcrEngine"/>: coincide con <see cref="RecognizeNearAsync"/>.</summary>
    public Task<OcrResult> RecognizeAroundPointAsync(CapturedImage image, double x, double y, CancellationToken ct) =>
        RecognizeNearAsync(image, x, y, ct);

    /// <summary>
    /// Interrompe l'eventuale riconoscimento in corso, ne attende la fine (massimo 5 s) e libera le sessioni native.
    /// Le richieste successive restituiscono un risultato vuoto.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();

        // Le sessioni ONNX non vanno liberate mentre un'inferenza le usa: si attende il semaforo.
        bool acquired = _gate.Wait(TimeSpan.FromSeconds(5));
        try
        {
            if (!acquired)
            {
                _log.Warn("OCR ONNX: il lavoro in corso non è terminato entro 5 s; le sessioni restano allocate fino alla chiusura.");
                return;
            }

            lock (_initLock)
            {
                _initialized = false;
                _detector?.Dispose();
                _recognizer?.Dispose();
                _detector = null;
                _recognizer = null;
            }
        }
        finally
        {
            if (acquired)
            {
                // Chi era in coda trova _disposed e restituisce un risultato vuoto.
                _gate.Release();
            }
        }
    }

    // ----- Inizializzazione -----

    private bool ModelFilesExist() =>
        File.Exists(Path.Combine(_modelsDirectory, DetModelFile))
        && File.Exists(Path.Combine(_modelsDirectory, RecModelFile))
        && File.Exists(Path.Combine(_modelsDirectory, KeysFile));

    /// <summary>Carica le sessioni una volta sola. Restituisce false (e registra il motivo) se qualcosa manca.</summary>
    private bool EnsureInitialized()
    {
        if (_disposed)
        {
            return false;
        }

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

    /// <summary>
    /// Esegue il lavoro su un thread dedicato a priorità BelowNormal, una richiesta alla volta. Il token passato al lavoro
    /// scatta sia per l'annullamento del chiamante sia per Dispose. Dopo Dispose restituisce whenDisposed.
    /// </summary>
    private async Task<T> RunOnWorkerAsync<T>(Func<CancellationToken, T> work, T whenDisposed, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return whenDisposed;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            var token = linked.Token;
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

                return work(token);
            }, token, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)
            .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
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

        if (image.Bgra.LongLength < (long)image.Width * image.Height * 4)
        {
            _log.Warn(string.Create(CultureInfo.InvariantCulture,
                $"OCR ONNX: buffer di {image.Bgra.LongLength} byte più corto di {image.Width}x{image.Height}x4."));
            return OcrResult.Empty(Name);
        }

        try
        {
            return await RunOnWorkerAsync(token => Pipeline(image, focus, token), OcrResult.Empty(Name), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Annullato da Dispose.
            return OcrResult.Empty(Name);
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
            // Finestra delle dimensioni del percorso mirato, grigio scuro con una barra chiara: carica i kernel del
            // rilevatore e fa lavorare la post-elaborazione senza il costo di un'immagine grande.
            var window = NearWindowSize(1.25);
            using var zone = new SKBitmap(new SKImageInfo(window.Width, window.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            zone.Erase(new SKColor(40, 40, 40));
            using (var canvas = new SKCanvas(zone))
            using (var paint = new SKPaint { Color = new SKColor(220, 220, 220) })
            {
                canvas.DrawRect(40, window.Height / 2f - 6, 200, 12, paint);
            }

            double scale = CapScale(NearDetectionScale, zone.Width, zone.Height);
            _ = Detect(zone, RoundToMultiple32(zone.Width * scale), RoundToMultiple32(zone.Height * scale), CancellationToken.None);
            long detMs = sw.ElapsedMilliseconds;

            // Ritaglio di una riga (48 px di altezza è l'ingresso del riconoscitore).
            using var line = new SKBitmap(new SKImageInfo(240, 24, SKColorType.Bgra8888, SKAlphaType.Opaque));
            line.Erase(new SKColor(40, 40, 40));
            _ = _recognizer!.GetTextLine(line);
            _log.Info(string.Create(CultureInfo.InvariantCulture,
                $"OCR ONNX riscaldato in {sw.ElapsedMilliseconds} ms (rilevamento {detMs} ms su {zone.Width}x{zone.Height} a {scale:0.00}x, riconoscimento {sw.ElapsedMilliseconds - detMs} ms)."));
        }
        catch (Exception ex)
        {
            _log.Warn($"OCR ONNX: riscaldamento non riuscito ({ex.GetBaseException().Message}).");
        }
    }

    // ----- Pipeline -----

    /// <summary>Esito del rilevamento: quadrilateri in coordinate dell'immagine originale, in ordine di lettura.</summary>
    private sealed record Detection(List<SKPointI[]> Quads, double Scale, bool SecondPass);

    private OcrResult Pipeline(CapturedImage image, (double X, double Y)? focus, CancellationToken ct)
    {
        if (!EnsureInitialized())
        {
            return OcrResult.Empty(Name);
        }

        var total = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        using var source = BitmapUtils.FromCapturedImage(image);
        var whole = new SKRectI(0, 0, source.Width, source.Height);

        // 1. Rilevamento: tutta l'immagine, oppure solo una finestra attorno al punto (molto più veloce).
        var detSw = Stopwatch.StartNew();
        Detection detection;
        List<SKPointI[]> selected;
        bool widened = false;
        if (focus is { } f)
        {
            var window = NearWindow(source.Width, source.Height, f.X, f.Y, image.DpiScale);
            detection = DetectRegion(source, window, NearDetectionScale, ct);
            selected = SelectNear(detection.Quads, f.X, f.Y, out var best);

            // Riga puntata tagliata dalla finestra: si allarga (in orizzontale a tutta la larghezza, in verticale a tutta l'immagine).
            var row = best is null ? [] : RowGroup(best, detection.Quads);
            bool cutVertically = row.Any(q => TouchesEdge(q, window, whole, vertical: true));
            bool cutHorizontally = row.Any(q => TouchesEdge(q, window, whole, vertical: false));
            if (cutVertically || cutHorizontally)
            {
                widened = true;
                ct.ThrowIfCancellationRequested();
                window = cutVertically ? whole : new SKRectI(0, window.Top, source.Width, window.Bottom);
                detection = DetectRegion(source, window, cutVertically ? null : NearDetectionScale, ct);
                selected = SelectNear(detection.Quads, f.X, f.Y, out _);
            }

            // I riquadri tagliati dalla finestra (altre colonne, righe lontane) sono frammenti: meglio non leggerli.
            var finalWindow = window;
            selected = selected.Where(q => !TouchesEdge(q, finalWindow, whole, vertical: false)
                                        && !TouchesEdge(q, finalWindow, whole, vertical: true)).ToList();
        }
        else
        {
            detection = DetectRegion(source, whole, null, ct);
            selected = detection.Quads;
        }

        long detMs = detSw.ElapsedMilliseconds;
        ct.ThrowIfCancellationRequested();

        // 2. Riconoscimento dei ritagli presi dall'immagine originale.
        var recSw = Stopwatch.StartNew();
        var lines = Recognize(source, selected, ct);
        long recMs = recSw.ElapsedMilliseconds;

        LastTimings = new OnnxOcrTimings((int)total.ElapsedMilliseconds, (int)detMs, (int)recMs, detection.Quads.Count, selected.Count,
            detection.Scale, detection.SecondPass, widened);

        if (_log.IsDebugEnabled)
        {
            string where = focus is { } p ? string.Create(CultureInfo.InvariantCulture, $" attorno a ({p.X:0},{p.Y:0})") : string.Empty;
            _log.Debug(string.Create(CultureInfo.InvariantCulture,
                $"OCR ONNX: {image.Width}x{image.Height}{where}, scala {detection.Scale:0.00}x{(detection.SecondPass ? " (secondo passaggio)" : "")}{(widened ? " (finestra allargata)" : "")}, {detection.Quads.Count} riquadri, {selected.Count} riconosciuti, {lines.Count} righe valide; rilevamento {detMs} ms, riconoscimento {recMs} ms, totale {total.ElapsedMilliseconds} ms."));
            foreach (var l in lines)
            {
                _log.Debug(string.Create(CultureInfo.InvariantCulture,
                    $"  [{l.Box.X:0},{l.Box.Y:0} {l.Box.Width:0}x{l.Box.Height:0}] {l.Confidence:0.00} '{l.Text}'"));
            }
        }

        return new OcrResult(lines, Name, (int)total.ElapsedMilliseconds);
    }

    /// <summary>
    /// Rileva le righe nel rettangolo richiesto dell'immagine. fixedScale null = scala automatica (lato corto portato a
    /// <see cref="DetectionShortSideTarget"/>); in entrambi i casi valgono i limiti di lato e di pixel. Se le righe trovate
    /// sono basse (testo piccolo) o assenti, il rilevamento viene ripetuto a 2x quando i limiti lo consentono.
    /// </summary>
    private Detection DetectRegion(SKBitmap source, SKRectI region, double? fixedScale, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool isWhole = region.Left == 0 && region.Top == 0 && region.Width == source.Width && region.Height == source.Height;
        SKBitmap regionBitmap = isWhole ? source : BitmapUtils.CopyRegion(source, region);
        SKBitmap? work = null;
        try
        {
            work = BitmapUtils.Letterbox(regionBitmap, LetterboxMinHeight, LetterboxMaxWidthHeightRatio, out int letterboxTop);
            int w = work.Width, h = work.Height;
            double scale = fixedScale is { } fs ? CapScale(fs, w, h) : AutoScale(w, h);
            var quads = Detect(work, RoundToMultiple32(w * scale), RoundToMultiple32(h * scale), ct);
            bool secondPass = false;

            if ((quads.Count == 0 || MedianHeight(quads) < SmallTextHeightPx) && scale < 2.0)
            {
                double scale2 = CapScale(2.0, w, h);
                if (scale2 >= scale * 1.2)
                {
                    ct.ThrowIfCancellationRequested();
                    var quads2 = Detect(work, RoundToMultiple32(w * scale2), RoundToMultiple32(h * scale2), ct);
                    if (quads2.Count >= quads.Count)
                    {
                        quads = quads2;
                        scale = scale2;
                        secondPass = true;
                    }
                }
            }

            // Riporta i quadrilateri sull'immagine originale (si toglie la banda del letterbox, si aggiunge l'origine).
            var mapped = new List<SKPointI[]>(quads.Count);
            foreach (var q in quads)
            {
                var m = new SKPointI[4];
                for (int i = 0; i < 4; i++)
                {
                    m[i] = new SKPointI(
                        Math.Clamp(q[i].X + region.Left, 0, source.Width),
                        Math.Clamp(q[i].Y - letterboxTop + region.Top, 0, source.Height));
                }

                if (QuadHeight(m) >= 2 && QuadWidth(m) >= 2)
                {
                    mapped.Add(m);
                }
            }

            return new Detection(mapped, scale, secondPass);
        }
        finally
        {
            if (work is not null && !ReferenceEquals(work, regionBitmap))
            {
                work.Dispose();
            }

            if (!ReferenceEquals(regionBitmap, source))
            {
                regionBitmap.Dispose();
            }
        }
    }

    private List<SKPointI[]> Detect(SKBitmap work, int dstW, int dstH, CancellationToken ct)
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
        return boxes.Where(b => b.BoxPoints is { Length: 4 } && QuadHeight(b.BoxPoints) >= 2 && QuadWidth(b.BoxPoints) >= 2)
                    .Select(b => b.BoxPoints)
                    .ToList();
    }

    /// <summary>
    /// Riquadri da riconoscere nel percorso mirato: quelli la cui fascia verticale contiene il punto oppure, se nessuna lo
    /// contiene, quelli che ne distano al massimo <see cref="NearDistanceInLineHeights"/> altezze di riga (l'altezza è quella
    /// della riga più vicina; la distanza orizzontale conta solo per l'ordine, come nel selettore della logica), più le
    /// righe attaccate che formano un blocco con loro (cartelli, paragrafi), fino a <see cref="NearMaxLines"/>.
    /// Il primo elemento restituito in best è la riga più vicina al punto.
    /// </summary>
    private List<SKPointI[]> SelectNear(List<SKPointI[]> quads, double px, double py, out SKPointI[]? best)
    {
        best = null;
        if (quads.Count == 0)
        {
            return [];
        }

        static double Dy(SKPointI[] q, double y)
        {
            var r = BoundsOf(q);
            return Math.Max(Math.Max(r.Y - y, 0), y - r.Bottom);
        }

        static double Dx(SKPointI[] q, double x)
        {
            var r = BoundsOf(q);
            return Math.Max(Math.Max(r.X - x, 0), x - r.Right);
        }

        double Score(SKPointI[] q) => Dy(q, py) + 0.25 * Dx(q, px);

        var nearest = quads.MinBy(Score)!;
        double nearestH = QuadHeight(nearest);
        double lineH = Dy(nearest, py) <= 3 * nearestH ? nearestH : MedianHeight(quads);
        double maxDist = NearDistanceInLineHeights * Math.Max(lineH, 4);

        // Se il punto cade dentro la fascia verticale di qualche riga bastano quelle (etichetta e scorciatoia di un menu);
        // altrimenti le righe entro la distanza massima (punto fra due righe).
        var containing = quads.Where(q => Dy(q, py) <= 0).ToList();
        var primary = containing.Count > 0 ? containing : quads.Where(q => Dy(q, py) <= maxDist).ToList();
        var chosen = new HashSet<SKPointI[]>(primary.OrderBy(Score).Take(Math.Max(1, NearMaxLines)));
        if (chosen.Count == 0)
        {
            return [];
        }

        best = chosen.MinBy(Score);

        // Espansione a blocco: righe di altezza simile, sovrapposte in orizzontale, con uno spazio verticale minimo.
        bool added = true;
        while (added && chosen.Count < NearMaxLines)
        {
            added = false;
            foreach (var q in quads)
            {
                if (chosen.Contains(q) || !chosen.Any(c => SameBlock(c, q)))
                {
                    continue;
                }

                chosen.Add(q);
                added = true;
                if (chosen.Count >= NearMaxLines)
                {
                    break;
                }
            }
        }

        // Ordine di lettura originale.
        return quads.Where(chosen.Contains).ToList();
    }

    private bool SameBlock(SKPointI[] a, SKPointI[] b)
    {
        var ra = BoundsOf(a);
        var rb = BoundsOf(b);
        double ha = QuadHeight(a), hb = QuadHeight(b);
        double ratio = hb / Math.Max(1, ha);
        if (ratio < 0.6 || ratio > 1.67)
        {
            return false;
        }

        bool overlapX = Math.Min(ra.Right, rb.Right) - Math.Max(ra.X, rb.X) > 0;
        double gap = Math.Max(rb.Y - ra.Bottom, ra.Y - rb.Bottom);
        return overlapX && gap < BlockGapInLineHeights * Math.Max(ha, hb);
    }

    /// <summary>
    /// La riga visiva del riquadro best: best più i riquadri sulla stessa fascia orizzontale collegati da spazi minori di
    /// un'altezza di riga (parole dello stesso cartello). Serve a capire se la finestra taglia la riga puntata.
    /// </summary>
    private static List<SKPointI[]> RowGroup(SKPointI[] best, List<SKPointI[]> quads)
    {
        var group = new List<SKPointI[]> { best };
        bool added = true;
        while (added)
        {
            added = false;
            foreach (var q in quads)
            {
                if (group.Contains(q))
                {
                    continue;
                }

                var rq = BoundsOf(q);
                double hq = QuadHeight(q);
                foreach (var g in group)
                {
                    var rg = BoundsOf(g);
                    double h = Math.Max(hq, QuadHeight(g));
                    double overlapY = Math.Min(rq.Bottom, rg.Bottom) - Math.Max(rq.Y, rg.Y);
                    double gapX = Math.Max(rq.X - rg.Right, rg.X - rq.Right);
                    if (overlapY >= 0.5 * Math.Min(rq.Height, rg.Height) && gapX < h)
                    {
                        group.Add(q);
                        added = true;
                        break;
                    }
                }
            }
        }

        return group;
    }

    /// <summary>True se il quadrilatero tocca un bordo della finestra che non è anche bordo dell'immagine.</summary>
    private static bool TouchesEdge(SKPointI[] q, SKRectI window, SKRectI whole, bool vertical)
    {
        const int Margin = 2;
        var r = BoundsOf(q);
        if (vertical)
        {
            return (window.Top > whole.Top && r.Y <= window.Top + Margin)
                || (window.Bottom < whole.Bottom && r.Bottom >= window.Bottom - Margin);
        }

        return (window.Left > whole.Left && r.X <= window.Left + Margin)
            || (window.Right < whole.Right && r.Right >= window.Right - Margin);
    }

    /// <summary>Finestra del percorso mirato, centrata sul punto e contenuta nell'immagine.</summary>
    private SKRectI NearWindow(int width, int height, double px, double py, double dpiScale)
    {
        var size = NearWindowSize(dpiScale);
        int ww = Math.Min(width, size.Width), wh = Math.Min(height, size.Height);
        int left = Math.Clamp((int)Math.Round(px - ww / 2.0), 0, width - ww);
        int top = Math.Clamp((int)Math.Round(py - wh / 2.0), 0, height - wh);
        return new SKRectI(left, top, left + ww, top + wh);
    }

    private SKSizeI NearWindowSize(double dpiScale)
    {
        double s = dpiScale is > 0.5 and < 5 ? dpiScale : 1.0;
        return new SKSizeI((int)Math.Round(NearWindowWidth * s), (int)Math.Round(NearWindowHeight * s));
    }

    private List<OcrLine> Recognize(SKBitmap source, List<SKPointI[]> quads, CancellationToken ct)
    {
        var result = new List<OcrLine>(quads.Count);
        if (quads.Count == 0)
        {
            return result;
        }

        var crops = new List<SKBitmap>(quads.Count);
        var cropInfo = new List<(CropFrame Frame, int Width, int Height, InkBox? Ink)>(quads.Count);
        try
        {
            foreach (var quad in quads)
            {
                ct.ThrowIfCancellationRequested();
                var crop = BitmapUtils.CropQuad(source, quad, out var frame);
                if (crop is null)
                {
                    continue;
                }

                var ink = BitmapUtils.FindInk(crop);
                cropInfo.Add((frame, crop.Width, crop.Height, ink));

                if (RecognitionHorizontalStretch > 1.01)
                {
                    var stretched = BitmapUtils.StretchHorizontally(crop, RecognitionHorizontalStretch);
                    crop.Dispose();
                    crop = stretched;
                }

                crops.Add(crop);
            }

            if (crops.Count == 0)
            {
                return result;
            }

            TextLine[] textLines = _recognizer!.GetTextLines(crops.ToArray(), Math.Max(1, RecognitionParallelism), null, ct);

            for (int i = 0; i < textLines.Length && i < cropInfo.Count; i++)
            {
                var info = cropInfo[i];
                var line = BuildLine(textLines[i], info.Frame, info.Width, info.Height, info.Ink, source.Width, source.Height);
                if (line is not null)
                {
                    result.Add(line);
                }
            }

            result = MergeRowFragments(result);
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
    private OcrLine? BuildLine(TextLine textLine, CropFrame frame, int cropW, int cropH, InkBox? ink, int imageW, int imageH)
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
        if (text.Length == 0 || !LatinTextFilter.HasLetterOrDigit(text))
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

        // Riquadro della riga: la zona con inchiostro se trovata (confrontabile con Windows.Media.Ocr), altrimenti il ritaglio.
        var box = ink ?? new InkBox(0, 0, cropW, cropH);
        var lineRect = ClampRect(MapRect(frame, box.Left, box.Top, box.Right, box.Bottom), imageW, imageH);
        var words = BuildWords(kept, frame, cropW, box, lineRect, imageW, imageH);
        return new OcrLine(text, lineRect, words, confidence);
    }

    /// <summary>
    /// Unisce le righe che il rilevatore ha spezzato in parole: stessa fascia orizzontale (sovrapposizione verticale di
    /// almeno metà, altezze simili) e spazio fra i riquadri minore di <see cref="WordMergeGapInLineHeights"/> altezze.
    /// </summary>
    private List<OcrLine> MergeRowFragments(List<OcrLine> lines)
    {
        if (lines.Count < 2 || WordMergeGapInLineHeights <= 0)
        {
            return lines;
        }

        var work = new List<OcrLine>(lines);
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < work.Count && !merged; i++)
            {
                for (int j = 0; j < work.Count && !merged; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    OcrLine a = work[i], b = work[j];
                    if (a.Box.CenterX > b.Box.CenterX)
                    {
                        continue;
                    }

                    double h = Math.Max(a.Box.Height, b.Box.Height);
                    double ratio = b.Box.Height / Math.Max(1, a.Box.Height);
                    double overlapY = Math.Min(a.Box.Bottom, b.Box.Bottom) - Math.Max(a.Box.Y, b.Box.Y);
                    double gap = b.Box.X - a.Box.Right;
                    if (ratio < 0.6 || ratio > 1.67
                        || overlapY < 0.5 * Math.Min(a.Box.Height, b.Box.Height)
                        || gap > WordMergeGapInLineHeights * h || gap < -0.3 * h)
                    {
                        continue;
                    }

                    int na = a.Text.Length, nb = b.Text.Length;
                    double? conf = a.Confidence is { } ca && b.Confidence is { } cb ? (ca * na + cb * nb) / Math.Max(1, na + nb) : null;
                    var joined = new OcrLine(a.Text + " " + b.Text, Union(a.Box, b.Box), a.Words.Concat(b.Words).ToList(), conf);
                    work[Math.Min(i, j)] = joined;
                    work.RemoveAt(Math.Max(i, j));
                    merged = true;
                }
            }
        }

        return work;
    }

    /// <summary>
    /// Spezza la riga in parole sugli spazi e stima il riquadro di ciascuna dalle colonne CTC del riconoscitore
    /// (frazioni della larghezza del ritaglio), con l'altezza della zona di inchiostro.
    /// </summary>
    private static IReadOnlyList<OcrWord> BuildWords(List<(string Text, float Score, double Start, double End)> kept, CropFrame frame,
        int cropW, InkBox box, ImageRect lineRect, int imageW, int imageH)
    {
        var words = new List<OcrWord>();
        var current = new System.Text.StringBuilder();
        double wordStart = -1, wordEnd = -1;

        void Flush()
        {
            if (current.Length > 0 && wordStart >= 0)
            {
                double u0 = Math.Clamp(wordStart * cropW, box.Left, box.Right);
                double u1 = Math.Clamp(wordEnd * cropW, u0, box.Right);
                var r = ClampRect(MapRect(frame, u0, box.Top, u1, box.Bottom), imageW, imageH);
                words.Add(new OcrWord(current.ToString(), Intersect(r, lineRect)));
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

    /// <summary>Scala automatica: lato corto portato a <see cref="DetectionShortSideTarget"/>, entro 1x..MaxUpscale e i limiti.</summary>
    private double AutoScale(int width, int height)
    {
        double s = DetectionShortSideTarget / (double)Math.Max(1, Math.Min(width, height));
        s = Math.Clamp(s, 1.0, Math.Max(1.0, MaxUpscale));
        return CapScale(s, width, height);
    }

    /// <summary>Applica i limiti di lato lungo e di pixel totali al fattore richiesto.</summary>
    private double CapScale(double scale, int width, int height)
    {
        double s = Math.Min(scale, MaxDetectionSide / (double)Math.Max(1, Math.Max(width, height)));
        if (MaxDetectionPixels > 0)
        {
            s = Math.Min(s, Math.Sqrt(MaxDetectionPixels / Math.Max(1.0, (double)width * height)));
        }

        return Math.Max(0.1, s);
    }

    private static int RoundToMultiple32(double value) => Math.Max(32, (int)Math.Round(value / 32.0) * 32);

    private static double QuadWidth(SKPointI[] q) => BitmapUtils.Distance(q[0], q[1]);
    private static double QuadHeight(SKPointI[] q) => BitmapUtils.Distance(q[0], q[3]);

    private static double MedianHeight(IReadOnlyList<SKPointI[]> quads)
    {
        if (quads.Count == 0)
        {
            return 0;
        }

        var heights = quads.Select(QuadHeight).OrderBy(v => v).ToArray();
        int mid = heights.Length / 2;
        return heights.Length % 2 == 1 ? heights[mid] : (heights[mid - 1] + heights[mid]) / 2.0;
    }

    private static ImageRect BoundsOf(SKPointI[] q)
    {
        int minX = q.Min(p => p.X), maxX = q.Max(p => p.X);
        int minY = q.Min(p => p.Y), maxY = q.Max(p => p.Y);
        return new ImageRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Rettangolo che racchiude il rettangolo [u0,u1]x[v0,v1] del ritaglio riportato sull'immagine.</summary>
    private static ImageRect MapRect(CropFrame frame, double u0, double v0, double u1, double v1)
    {
        var pts = new[] { frame.Map(u0, v0), frame.Map(u1, v0), frame.Map(u1, v1), frame.Map(u0, v1) };
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        return new ImageRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static ImageRect ClampRect(ImageRect r, int w, int h)
    {
        double x = Math.Clamp(r.X, 0, w), y = Math.Clamp(r.Y, 0, h);
        double right = Math.Clamp(r.Right, x, w), bottom = Math.Clamp(r.Bottom, y, h);
        return new ImageRect(x, y, right - x, bottom - y);
    }

    private static ImageRect Union(ImageRect a, ImageRect b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new ImageRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    private static ImageRect Intersect(ImageRect a, ImageRect b)
    {
        double x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.Right, b.Right), bottom = Math.Min(a.Bottom, b.Bottom);
        return right <= x || bottom <= y ? a : new ImageRect(x, y, right - x, bottom - y);
    }
}

/// <summary>Tempi e contatori dell'ultima richiesta OCR.</summary>
/// <param name="TotalMs">Tempo complessivo della pipeline.</param>
/// <param name="DetectMs">Rilevamento (uno o più passaggi).</param>
/// <param name="RecognizeMs">Ritaglio e riconoscimento.</param>
/// <param name="BoxesDetected">Riquadri trovati (nell'ultimo rilevamento).</param>
/// <param name="BoxesRecognized">Riquadri passati al riconoscitore.</param>
/// <param name="DetectionScale">Fattore di ingrandimento usato per il rilevamento.</param>
/// <param name="SecondPass">True se il rilevamento è stato ripetuto a 2x.</param>
/// <param name="Widened">True se nel percorso mirato la finestra è stata allargata perché tagliava il testo.</param>
public sealed record OnnxOcrTimings(int TotalMs, int DetectMs, int RecognizeMs, int BoxesDetected, int BoxesRecognized, double DetectionScale, bool SecondPass, bool Widened = false);
