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
public sealed class WindowsOcrEngine : IOcrEngine
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
    /// Secondo passaggio per testo a basso contrasto (voci disabilitate, grigio su grigio): ritaglio di circa 400x120
    /// intorno a (x, y), 2x, scala di grigi e stiramento del contrasto. (x, y) sono in pixel dell'immagine <paramref name="image"/>
    /// (le stesse coordinate dei rettangoli restituiti). Da usare solo quando il primo passaggio non trova nulla sulla riga.
    /// </summary>
    public Task<OcrResult> RecognizeLowContrastAsync(CapturedImage image, double x, double y, CancellationToken ct) =>
        RunAsync(image, (img, maxDim, pool) => OcrPreprocessor.PrepareLowContrast(img, x, y, maxDim, pool), "basso contrasto", ct);

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

    /// <summary>Riporta righe e parole alle coordinate dell'immagine originale. Testo della riga = parole unite da spazi.</summary>
    private static IReadOnlyList<OcrLine> MapLines(WinOcr.OcrResult result, PreparedImage prepared)
    {
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
                words.Add(new OcrWord(text, prepared.ToOriginal(rect.X, rect.Y, rect.Width, rect.Height)));
            }

            if (words.Count == 0)
            {
                continue;
            }

            lines.Add(new OcrLine(string.Join(' ', words.Select(w => w.Text)), Union(words), words, null));
        }

        return lines;
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
