using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using WinFoundation = global::Windows.Foundation;
using WinGlobalization = global::Windows.Globalization;
using WinImaging = global::Windows.Graphics.Imaging;
using WinOcr = global::Windows.Media.Ocr;

namespace PuntaEAscolta.Windows.Ocr;

/// <summary>
/// Motore OCR basato su Windows.Media.Ocr con il pre-trattamento misurato in docs/research.
/// Un solo <c>RecognizeAsync</c> alla volta (il motore WinRT non e rientrante per istanza).
/// Le coordinate restituite sono quelle dell'immagine ORIGINALE passata dal chiamante.
/// Non solleva eccezioni verso il chiamante, salvo <see cref="OperationCanceledException"/> quando il token viene annullato.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine, IPointOcrEngine
{
    /// <summary>Nome con cui il motore compare in <see cref="OcrResult.EngineName"/>.</summary>
    public const string EngineName = "windows";

    /// <summary>
    /// Modalita alfa con cui i pixel BGRA vengono passati al motore. Il pre-trattamento produce sempre alfa 255,
    /// quindi Ignore e Premultiplied sono equivalenti; Ignore protegge anche dal caso di catture con alfa 0.
    /// </summary>
    private const WinImaging.BitmapAlphaMode AlphaMode = WinImaging.BitmapAlphaMode.Ignore;

    private readonly Func<string> _languageTag;
    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _engineLock = new();

    private WinOcr.OcrEngine? _engine;
    private string? _engineTag;
    private volatile bool _disposed;

    /// <param name="languageTag">Restituisce il codice lingua richiesto (es. "it-IT"); letto a ogni riconoscimento, il motore viene ricreato quando cambia.</param>
    /// <param name="log">Registro diagnostico.</param>
    public WindowsOcrEngine(Func<string> languageTag, ILog log)
    {
        _languageTag = languageTag ?? throw new ArgumentNullException(nameof(languageTag));
        _log = log ?? NullLog.Instance;
    }

    public string Name => EngineName;

    /// <summary>Vero se e stato possibile creare un motore per una lingua installata. Non solleva eccezioni.</summary>
    public bool IsAvailable
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                return GetOrCreateEngine() != null;
            }
            catch (Exception ex)
            {
                _log.Warn("OCR Windows: verifica di disponibilita fallita: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Primo passaggio: immagine intera, ingrandimento adattivo alla scala del monitor, margine, colore conservato.</summary>
    public Task<OcrResult> RecognizeAsync(CapturedImage image, CancellationToken ct) =>
        RunAsync(image, static (img, maxDim, pool) => OcrPreprocessor.PrepareStandard(img, maxDim, pool), "standard", ct);

    /// <summary>
    /// Secondo passaggio per testo a basso contrasto (voci disabilitate, grigio su grigio): fascia alta 120 px sulla riga di (x, y)
    /// e larga quanto l'immagine, 2x, scala di grigi e stiramento del contrasto misurato sul ritaglio 400x120 attorno al punto.
    /// Le righe tagliate dai bordi superiore e inferiore della fascia vengono scartate. (x, y) sono in pixel dell'immagine
    /// <paramref name="image"/> (le stesse coordinate dei rettangoli restituiti). Da usare solo quando il primo passaggio non
    /// trova nulla sulla riga.
    /// </summary>
    public Task<OcrResult> RecognizeLowContrastAsync(CapturedImage image, double x, double y, CancellationToken ct) =>
        RunAsync(image, (img, maxDim, pool) => OcrPreprocessor.PrepareLowContrast(img, x, y, maxDim, pool), "basso contrasto", ct);

    /// <inheritdoc />
    public Task<OcrResult> RecognizeAroundPointAsync(CapturedImage image, double x, double y, CancellationToken ct) =>
        RecognizeLowContrastAsync(image, x, y, ct);

    public void Dispose()
    {
        // Il semaforo non viene eliminato di proposito: un riconoscimento in attesa non deve trovarsi con un oggetto
        // eliminato sotto i piedi; SemaphoreSlim senza WaitHandle non possiede risorse di sistema.
        _disposed = true;
        lock (_engineLock)
        {
            _engine = null;
            _engineTag = null;
        }
    }

    private Task<OcrResult> RunAsync(
        CapturedImage image,
        Func<CapturedImage, uint, ArrayPool<byte>, PreparedImage> prepare,
        string passName,
        CancellationToken ct)
    {
        if (_disposed)
        {
            return Task.FromResult(OcrResult.Empty(EngineName));
        }

        if (!IsValidImage(image))
        {
            _log.Warn("OCR Windows: immagine non valida (dimensioni o buffer incoerenti), passaggio " + passName + " saltato.");
            return Task.FromResult(OcrResult.Empty(EngineName));
        }

        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<OcrResult>(ct);
        }

        // Misurato: OcrEngine.RecognizeAsync di WinRT completa in modo SINCRONO sul thread chiamante (30-90 ms).
        // Tutto il lavoro (GDI+ e OCR) va quindi sul pool, cosi il chiamante non resta mai bloccato.
        return Task.Run(() => RunCoreAsync(image, prepare, passName, ct), ct);
    }

    private async Task<OcrResult> RunCoreAsync(
        CapturedImage image,
        Func<CapturedImage, uint, ArrayPool<byte>, PreparedImage> prepare,
        string passName,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        bool entered = false;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;

            if (_disposed)
            {
                return OcrResult.Empty(EngineName);
            }

            WinOcr.OcrEngine? engine = GetOrCreateEngine();
            if (engine == null)
            {
                return OcrResult.Empty(EngineName);
            }

            ct.ThrowIfCancellationRequested();
            uint maxDimension = WinOcr.OcrEngine.MaxImageDimension;
            ArrayPool<byte> pool = ArrayPool<byte>.Shared;
            PreparedImage prepared = prepare(image, maxDimension, pool);
            long prepareMs = stopwatch.ElapsedMilliseconds;

            WinOcr.OcrResult winResult;
            try
            {
                ct.ThrowIfCancellationRequested();
                using WinImaging.SoftwareBitmap bitmap = WinImaging.SoftwareBitmap.CreateCopyFromBuffer(
                    prepared.Bgra.AsBuffer(0, prepared.ByteLength),
                    WinImaging.BitmapPixelFormat.Bgra8,
                    prepared.Width,
                    prepared.Height,
                    AlphaMode);
                winResult = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            }
            finally
            {
                pool.Return(prepared.Bgra);
            }

            IReadOnlyList<OcrLine> lines = MapLines(winResult, prepared);
            if (prepared.OriginalWidth > 0 && prepared.OriginalHeight > 0)
            {
                // Righe tagliate dai bordi della fascia del secondo passaggio (non dai bordi della cattura): frammenti da scartare.
                lines = OcrPreprocessor.DropLinesCutByCrop(lines, prepared.Source, prepared.OriginalWidth, prepared.OriginalHeight);
            }
            stopwatch.Stop();

            if (_log.IsDebugEnabled)
            {
                _log.Debug(
                    "OCR Windows (" + passName + "): " + lines.Count + " righe in " + stopwatch.ElapsedMilliseconds + " ms" +
                    " (pre-trattamento " + prepareMs + " ms, " + prepared.Width + "x" + prepared.Height + ", scala " + prepared.Scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "): " +
                    string.Join(" | ", lines.Select(l => l.Text)));
            }

            return new OcrResult(lines, EngineName, (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            return OcrResult.Empty(EngineName);
        }
        catch (Exception ex)
        {
            _log.Error("OCR Windows: errore nel passaggio " + passName + ".", ex);
            return OcrResult.Empty(EngineName);
        }
        finally
        {
            if (entered)
            {
                try
                {
                    _gate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Nulla da fare: l'oggetto e stato eliminato durante il riconoscimento.
                }
            }
        }
    }

    private static bool IsValidImage(CapturedImage? image) =>
        image is { Width: > 0, Height: > 0, Bgra: not null } &&
        (long)image.Width * image.Height * 4 <= image.Bgra.Length;

    /// <summary>
    /// Riporta righe e parole alle coordinate dell'immagine originale. Testo della riga = parole unite da spazi.
    /// Quando il motore stima un'inclinazione del testo (<see cref="WinOcr.OcrResult.TextAngle"/>, anche 3 gradi spuri su
    /// schermate d'interfaccia) i riquadri sono nel sistema raddrizzato: si ruotano di nuovo attorno al centro dell'immagine,
    /// altrimenti una riga lontana dal centro risulta spostata di 10-25 px e il puntatore finisce sulla riga sbagliata.
    /// </summary>
    private static IReadOnlyList<OcrLine> MapLines(WinOcr.OcrResult result, PreparedImage prepared)
    {
        double angle = SafeTextAngle(result);
        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (WinOcr.OcrLine line in result.Lines)
        {
            var words = new List<OcrWord>(line.Words.Count);
            foreach (WinOcr.OcrWord word in line.Words)
            {
                string text = word.Text?.Trim() ?? string.Empty;
                if (text.Length == 0)
                {
                    continue;
                }

                WinFoundation.Rect rect = word.BoundingRect;
                var (x, y) = UndoTextAngle(rect.X, rect.Y, rect.Width, rect.Height, prepared.Width, prepared.Height, angle);
                words.Add(new OcrWord(text, prepared.ToOriginal(x, y, rect.Width, rect.Height)));
            }

            if (words.Count == 0)
            {
                continue;
            }

            lines.Add(new OcrLine(string.Join(' ', words.Select(w => w.Text)), Union(words), words, null));
        }

        return lines;
    }

    private static double SafeTextAngle(WinOcr.OcrResult result)
    {
        try
        {
            double? angle = result.TextAngle;
            return angle is { } a && double.IsFinite(a) && Math.Abs(a) >= 0.01 && Math.Abs(a) <= 45 ? a : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Riporta l'angolo superiore sinistro di un riquadro dal sistema raddrizzato del motore a quello dell'immagine passata:
    /// il centro del riquadro ruota in senso orario di <paramref name="angleDegrees"/> attorno al centro dell'immagine
    /// (misurato: con TextAngle 3,2 la riga "FF0000" torna da y 103 a 145 su 288, cioè sulla sua riga vera).
    /// </summary>
    internal static (double X, double Y) UndoTextAngle(double x, double y, double width, double height, double imageWidth, double imageHeight, double angleDegrees)
    {
        if (angleDegrees == 0) return (x, y);
        double rad = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = imageWidth / 2.0, cy = imageHeight / 2.0;
        double dx = x + width / 2.0 - cx, dy = y + height / 2.0 - cy;
        double rx = cx + dx * cos - dy * sin;
        double ry = cy + dx * sin + dy * cos;
        return (rx - width / 2.0, ry - height / 2.0);
    }

    private static ImageRect Union(List<OcrWord> words)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (OcrWord word in words)
        {
            left = Math.Min(left, word.Box.X);
            top = Math.Min(top, word.Box.Y);
            right = Math.Max(right, word.Box.Right);
            bottom = Math.Max(bottom, word.Box.Bottom);
        }

        return new ImageRect(left, top, right - left, bottom - top);
    }

    /// <summary>Restituisce il motore corrente, ricreandolo se il codice lingua richiesto e cambiato.</summary>
    private WinOcr.OcrEngine? GetOrCreateEngine()
    {
        string requested = ReadRequestedTag();
        lock (_engineLock)
        {
            if (_disposed)
            {
                return null;
            }

            if (_engine != null && string.Equals(_engineTag, requested, StringComparison.OrdinalIgnoreCase))
            {
                return _engine;
            }

            WinOcr.OcrEngine? created = CreateEngine(requested);
            if (created != null)
            {
                _engine = created;
            }
            else if (_engine != null)
            {
                _log.Warn("OCR Windows: impossibile creare il motore per '" + requested + "', resto sulla lingua " + _engine.RecognizerLanguage.LanguageTag + ".");
            }

            // Si memorizza il codice richiesto anche in caso di fallimento, per non ritentare (e registrare) a ogni chiamata.
            _engineTag = requested;
            return _engine;
        }
    }

    private string ReadRequestedTag()
    {
        try
        {
            return (_languageTag() ?? string.Empty).Trim();
        }
        catch (Exception ex)
        {
            _log.Warn("OCR Windows: lettura del codice lingua fallita, uso la lingua del profilo utente: " + ex.Message);
            return string.Empty;
        }
    }

    /// <summary>TryCreateFromLanguage(richiesta) -> TryCreateFromUserProfileLanguages -> prima lingua disponibile.</summary>
    private WinOcr.OcrEngine? CreateEngine(string requested)
    {
        try
        {
            WinOcr.OcrEngine? engine = null;
            if (requested.Length > 0)
            {
                if (WinGlobalization.Language.IsWellFormed(requested))
                {
                    engine = WinOcr.OcrEngine.TryCreateFromLanguage(new WinGlobalization.Language(requested));
                    if (engine == null)
                    {
                        _log.Warn("OCR Windows: la lingua '" + requested + "' non ha il riconoscitore OCR installato, provo la lingua del profilo utente.");
                    }
                }
                else
                {
                    _log.Warn("OCR Windows: codice lingua '" + requested + "' non valido, provo la lingua del profilo utente.");
                }
            }

            engine ??= WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();

            if (engine == null)
            {
                IReadOnlyList<WinGlobalization.Language> available = WinOcr.OcrEngine.AvailableRecognizerLanguages;
                if (available.Count > 0)
                {
                    engine = WinOcr.OcrEngine.TryCreateFromLanguage(available[0]);
                }
            }

            if (engine == null)
            {
                _log.Warn("OCR Windows: nessuna lingua OCR installata nel sistema, motore non disponibile.");
            }
            else
            {
                _log.Info(
                    "OCR Windows: motore pronto, lingua " + engine.RecognizerLanguage.LanguageTag +
                    " (richiesta '" + requested + "'), dimensione massima immagine " + WinOcr.OcrEngine.MaxImageDimension + " px.");
            }

            return engine;
        }
        catch (Exception ex)
        {
            _log.Error("OCR Windows: creazione del motore fallita.", ex);
            return null;
        }
    }
}
