using System.Diagnostics;
using System.Text;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Resolution;
using PuntaEAscolta.Logic.Text;

namespace PuntaEAscolta.Logic.Reading;

/// <summary>
/// Decide che cosa leggere per una richiesta, senza parlare (flusso di DESIGN.md 2.1):
/// 1. selezione sotto il puntatore, 2. elemento di accessibilità, 3. suggerimento visibile, 4. OCR della zona.
/// Ogni fase è protetta singolarmente da eccezioni, tempo massimo e annullamento: un guasto fa passare alla fase successiva.
/// Non attiva finestre, non tocca il focus, non registra il testo se non a livello Debug.
/// </summary>
public sealed class TextResolver : ITextResolver
{
    /// <summary>Margine aggiunto al rettangolo di un suggerimento senza testo prima dell'OCR.</summary>
    public const int TooltipInflatePx = 8;

    /// <summary>Dimensioni (al 100%) sotto le quali un elemento UIA senza testo delimita l'OCR al suo rettangolo.</summary>
    public const int SmallElementWidth = 300;
    public const int SmallElementHeight = 120;

    /// <summary>Una riga OCR fino a questa lunghezza è pronunciata come etichetta, oltre come frase.</summary>
    public const int MaxLabelChars = 60;

    private readonly IUiTextSource _ui;
    private readonly IScreenCapture _capture;
    private readonly IOcrEngine _primaryOcr;
    private readonly IOcrEngine? _secondaryOcr;
    private readonly IClipboardSelectionReader? _clipboard;
    private readonly ISettingsStore _settings;
    private readonly ILog _log;

    public TextResolver(
        IUiTextSource ui,
        IScreenCapture capture,
        IOcrEngine primaryOcr,
        IOcrEngine? secondaryOcr,
        IClipboardSelectionReader? clipboard,
        ISettingsStore settings,
        ILog log)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _primaryOcr = primaryOcr ?? throw new ArgumentNullException(nameof(primaryOcr));
        _secondaryOcr = secondaryOcr;
        _clipboard = clipboard;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? NullLog.Instance;
    }

    /// <summary>Tempo massimo per ogni chiamata all'accessibilità.</summary>
    internal TimeSpan UiaTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Tempo massimo per ogni riconoscimento OCR.</summary>
    internal TimeSpan OcrTimeout { get; set; } = TimeSpan.FromMilliseconds(3000);

    /// <summary>Tempo massimo per il ripiego sugli appunti.</summary>
    internal TimeSpan ClipboardTimeout { get; set; } = TimeSpan.FromMilliseconds(2000);

    // Punti di innesto verso le funzioni pure della parte A: nel prodotto sono le classi statiche reali,
    // nei test possono essere sostituiti. Ogni chiamata è comunque protetta (una NotImplementedException = "nessun testo").
    internal Func<UiElementInfo, ReadingSettings, UiResolution?> ResolveUiElement { get; set; } = UiTextResolver.Resolve;
    internal Func<OcrResult, double, double, OcrSettings, bool, ImageRect?, OcrSelection?> SelectOcrText { get; set; } = PointerTextSelector.Select;
    internal Func<string, UiElementKind, ReadingSettings, string> CleanLabel { get; set; } = LabelCleaner.Clean;
    internal Func<string, string> StripEmoji { get; set; } = EmojiFilter.Strip;
    internal Func<string, LabelLanguageMode, string?> GuessLanguage { get; set; } = LanguageGuesser.Guess;

    public async Task<ReadOutcome> ResolveAsync(ReadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var settings = _settings.Current;
        var diag = new StringBuilder(160);
        diag.Append(request.Kind).Append('@').Append(request.Point.X).Append(',').Append(request.Point.Y);

        try
        {
            ct.ThrowIfCancellationRequested();
            var candidate = request.Kind switch
            {
                ReadRequestKind.Selection => await ResolveSelectionRequestAsync(settings, diag, ct).ConfigureAwait(false),
                ReadRequestKind.ZoneAroundPointer => await OcrZoneAsync(request.Point, settings, wholeZone: true, element: null, clipToElement: false, diag, ct).ConfigureAwait(false),
                _ => await ResolveAtPointerAsync(request.Point, settings, diag, ct).ConfigureAwait(false),
            };
            var outcome = Finish(candidate, settings, stopwatch, diag);
            if (outcome.HasText)
            {
                _log.Info($"Testo trovato da {outcome.Source} in {outcome.ElapsedMs} ms ({outcome.Text.Length} caratteri)");
                if (_log.IsDebugEnabled) _log.Debug($"Testo: \"{outcome.Text}\" | {outcome.Diagnostics}");
            }
            else
            {
                _log.Info($"Nessun testo in {outcome.ElapsedMs} ms | {outcome.Diagnostics}");
            }
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Debug($"Risoluzione annullata dopo {stopwatch.ElapsedMilliseconds} ms | {diag}");
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Errore imprevisto nella risoluzione del testo", ex);
            diag.Append(" errore:").Append(ex.GetType().Name);
            return ReadOutcome.Nothing((int)stopwatch.ElapsedMilliseconds, diag.ToString());
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Flussi
    // ---------------------------------------------------------------------------------------------

    private async Task<Candidate?> ResolveAtPointerAsync(ScreenPoint point, AppSettings settings, StringBuilder diag, CancellationToken ct)
    {
        // 1. Selezione: si legge solo se il puntatore è dentro uno dei rettangoli del testo selezionato.
        if (settings.Reading.ReadSelectionWhenPointerInside)
        {
            var selection = await GuardAsync("selezione", t => _ui.GetSelectionAsync(t), UiaTimeout, diag, ct).ConfigureAwait(false);
            if (selection is not null && !string.IsNullOrWhiteSpace(selection.Text)
                && selection.Bounds is { Count: > 0 } && selection.Bounds.Any(r => !r.IsEmpty && r.Contains(point)))
            {
                diag.Append(" -> Selection");
                return new Candidate(ReadSource.Selection, selection.Text, SpeechKind.Sentence, Sensitive: true);
            }
        }
        ct.ThrowIfCancellationRequested();

        // 2. Elemento sotto il puntatore. Il nome del processo arriva solo da qui: per i processi "solo OCR" si salta la decisione.
        var element = await GuardAsync("elemento", t => _ui.GetElementAtAsync(point, t), UiaTimeout, diag, ct).ConfigureAwait(false);
        bool ocrOnly = element is not null && IsOcrOnlyProcess(element.ProcessName, settings.Reading);
        bool uiaTriedWithoutText = false;
        if (element is not null)
        {
            diag.Append(' ').Append(element.Kind).Append('/').Append(element.ProcessName ?? "?")
                .Append(' ').Append(element.Bounds.Width).Append('x').Append(element.Bounds.Height);
            if (ocrOnly)
            {
                diag.Append(" (processo solo OCR: accessibilità saltata)");
            }
            else
            {
                var resolution = Safe("UiTextResolver", () => ResolveUiElement(element, settings.Reading));
                if (resolution is not null && !string.IsNullOrWhiteSpace(resolution.Text))
                {
                    diag.Append(" -> ").Append(resolution.Source);
                    return new Candidate(resolution.Source, resolution.Text, resolution.Kind, resolution.Sensitive);
                }
                diag.Append(" uia:niente");
                uiaTriedWithoutText = true;
            }
        }
        ct.ThrowIfCancellationRequested();

        // 3. Suggerimento visibile vicino al puntatore.
        var tooltip = await GuardAsync("suggerimento", t => _ui.FindTooltipAsync(point, t), UiaTimeout, diag, ct).ConfigureAwait(false);
        if (tooltip is not null)
        {
            if (!string.IsNullOrWhiteSpace(tooltip.Text))
            {
                string rawTip = tooltip.Text;
                var text = Safe("LabelCleaner", () => CleanLabel(rawTip, UiElementKind.ToolTip, settings.Reading)) ?? rawTip;
                if (string.IsNullOrWhiteSpace(text)) text = rawTip;
                diag.Append(" -> Tooltip");
                return new Candidate(ReadSource.Tooltip, text, text.Length <= MaxLabelChars ? SpeechKind.Label : SpeechKind.Sentence, Sensitive: false);
            }
            if (!tooltip.Bounds.IsEmpty)
            {
                var fromTooltip = await OcrTooltipAsync(tooltip.Bounds, point, settings, diag, ct).ConfigureAwait(false);
                if (fromTooltip is not null) return fromTooltip;
            }
        }
        ct.ThrowIfCancellationRequested();

        // 4. OCR della zona attorno al puntatore.
        return await OcrZoneAsync(point, settings, wholeZone: false, element, clipToElement: uiaTriedWithoutText, diag, ct).ConfigureAwait(false);
    }

    private async Task<Candidate?> ResolveSelectionRequestAsync(AppSettings settings, StringBuilder diag, CancellationToken ct)
    {
        var selection = await GuardAsync("selezione", t => _ui.GetSelectionAsync(t), UiaTimeout, diag, ct).ConfigureAwait(false);
        if (selection is not null && !string.IsNullOrWhiteSpace(selection.Text))
        {
            diag.Append(" -> Selection");
            return new Candidate(ReadSource.Selection, selection.Text, SpeechKind.Sentence, Sensitive: true);
        }
        ct.ThrowIfCancellationRequested();

        if (_clipboard is null)
        {
            diag.Append(" appunti:assenti");
            return null;
        }
        var copied = await GuardAsync("appunti", t => _clipboard.TryCopySelectionAsync(t), ClipboardTimeout, diag, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(copied))
        {
            diag.Append(" -> ClipboardSelection");
            return new Candidate(ReadSource.ClipboardSelection, copied, SpeechKind.Sentence, Sensitive: true);
        }
        return null;
    }

    private async Task<Candidate?> OcrTooltipAsync(ScreenRect bounds, ScreenPoint anchor, AppSettings settings, StringBuilder diag, CancellationToken ct)
    {
        var rect = bounds.Inflate(TooltipInflatePx, TooltipInflatePx);
        var image = Safe("cattura suggerimento", () => _capture.Capture(rect, anchor));
        if (image is null || image.Width <= 0 || image.Height <= 0 || image.Bgra is null)
        {
            diag.Append(" cattura-suggerimento:fallita");
            return null;
        }
        diag.Append(" ocr-suggerimento");
        var candidate = await RunOcrAsync(image, image.Width / 2.0, image.Height / 2.0, settings, wholeZone: true, clipTo: null, UiElementKind.ToolTip, diag, ct).ConfigureAwait(false);
        if (candidate is null) return null;
        diag.Append(" -> TooltipOcr");
        return candidate with { Source = ReadSource.TooltipOcr };
    }

    private async Task<Candidate?> OcrZoneAsync(ScreenPoint point, AppSettings settings, bool wholeZone, UiElementInfo? element, bool clipToElement, StringBuilder diag, CancellationToken ct)
    {
        double dpi = Safe("scala DPI", () => _capture.GetDpiScale(point), fallback: 1.0);
        if (dpi <= 0 || double.IsNaN(dpi) || double.IsInfinity(dpi)) dpi = 1.0;

        int width = Math.Max(16, (int)Math.Round(settings.Ocr.ZoneWidth * dpi));
        int height = Math.Max(16, (int)Math.Round(settings.Ocr.ZoneHeight * dpi));
        var zone = ScreenRect.Around(point, width, height);

        var image = Safe("cattura zona", () => _capture.Capture(zone, point));
        if (image is null || image.Width <= 0 || image.Height <= 0 || image.Bgra is null)
        {
            diag.Append(" cattura:fallita");
            return null;
        }
        diag.Append(" zona=").Append(image.Width).Append('x').Append(image.Height).Append(" dpi=").Append(dpi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

        var (px, py) = ToImageSpace(image, point);
        ImageRect? clip = null;
        if (clipToElement && element is not null && !element.Bounds.IsEmpty && IsSmallElement(element.Bounds, dpi))
        {
            clip = ToImageRect(image, element.Bounds);
            diag.Append(" ritaglio-elemento");
        }

        return await RunOcrAsync(image, px, py, settings, wholeZone, clip, element?.Kind, diag, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Riconoscimento con scelta del motore: primario (Windows) e poi secondario (ONNX) se il primario non trova nulla vicino al puntatore,
    /// oppure, in modalità Auto, se l'elemento è un'immagine o un'area di disegno e il primario ha trovato meno di due righe.
    /// </summary>
    private async Task<Candidate?> RunOcrAsync(CapturedImage image, double px, double py, AppSettings settings, bool wholeZone, ImageRect? clipTo, UiElementKind? elementKind, StringBuilder diag, CancellationToken ct)
    {
        var mode = settings.Ocr.Mode;
        IOcrEngine? primary = IsAvailable(_primaryOcr) ? _primaryOcr : null;
        IOcrEngine? secondary = _secondaryOcr is not null && IsAvailable(_secondaryOcr) ? _secondaryOcr : null;

        switch (mode)
        {
            case OcrMode.WindowsOnly:
                secondary = null;
                break;
            case OcrMode.OnnxOnly when secondary is not null:
                primary = null;
                break;
            case OcrMode.OnnxOnly:
                diag.Append(" (ONNX non disponibile: uso il motore primario)");
                break;
        }

        if (primary is null && secondary is null)
        {
            diag.Append(" ocr:nessun-motore");
            _log.Warn("Nessun motore OCR disponibile");
            return null;
        }

        OcrSelection? best = null;
        OcrResult? primaryResult = null;
        if (primary is not null)
        {
            primaryResult = await GuardAsync("ocr:" + primary.Name, async t => (OcrResult?)await primary.RecognizeAsync(image, t).ConfigureAwait(false), OcrTimeout, diag, ct).ConfigureAwait(false);
            if (primaryResult is not null)
            {
                diag.Append(" righe=").Append(primaryResult.Lines?.Count ?? 0);
                best = Safe("PointerTextSelector", () => SelectOcrText(primaryResult, px, py, settings.Ocr, wholeZone, clipTo));
                if (best is not null && string.IsNullOrWhiteSpace(best.Text)) best = null;
            }
        }
        ct.ThrowIfCancellationRequested();

        bool imageLike = elementKind is UiElementKind.Image or UiElementKind.Pane or UiElementKind.Custom or UiElementKind.Document;
        bool primaryFoundLittle = (primaryResult?.Lines?.Count ?? 0) < 2;
        bool useSecondary = primary is null || best is null || (imageLike && primaryFoundLittle);
        if (useSecondary && secondary is not null)
        {
            var secondaryResult = await GuardAsync("ocr:" + secondary.Name, async t => (OcrResult?)await secondary.RecognizeAsync(image, t).ConfigureAwait(false), OcrTimeout, diag, ct).ConfigureAwait(false);
            if (secondaryResult is not null)
            {
                diag.Append(" righe=").Append(secondaryResult.Lines?.Count ?? 0);
                var fromSecondary = Safe("PointerTextSelector", () => SelectOcrText(secondaryResult, px, py, settings.Ocr, wholeZone, clipTo));
                if (fromSecondary is not null && !string.IsNullOrWhiteSpace(fromSecondary.Text))
                {
                    best = fromSecondary;
                    diag.Append(" scelto=").Append(secondary.Name);
                }
            }
        }

        if (best is null)
        {
            diag.Append(" ocr:niente-vicino-al-puntatore");
            return null;
        }

        diag.Append(" -> ").Append(best.Source);
        var kind = best.Source == ReadSource.OcrLine && best.Text.Length <= MaxLabelChars ? SpeechKind.Label : SpeechKind.Sentence;
        return new Candidate(best.Source, best.Text, kind, Sensitive: false);
    }

    // ---------------------------------------------------------------------------------------------
    // Rifinitura del testo
    // ---------------------------------------------------------------------------------------------

    private ReadOutcome Finish(Candidate? candidate, AppSettings settings, Stopwatch stopwatch, StringBuilder diag)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Text))
            return ReadOutcome.Nothing((int)stopwatch.ElapsedMilliseconds, diag.ToString());

        var text = NormalizeWhitespace(candidate.Text);

        if (settings.Reading.StripEmoji)
        {
            var stripped = Safe("EmojiFilter", () => StripEmoji(text));
            if (stripped is not null) text = NormalizeWhitespace(stripped);
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            diag.Append(" solo-emoji");
            return ReadOutcome.Nothing((int)stopwatch.ElapsedMilliseconds, diag.ToString());
        }

        int max = settings.Reading.MaxCharsPerRead;
        if (max > 0 && text.Length > max)
        {
            text = TruncateAtWordBoundary(text, max);
            diag.Append(" troncato=").Append(text.Length);
        }

        var languageMode = candidate.Kind == SpeechKind.Label ? settings.Speech.LabelLanguage : LabelLanguageMode.Auto;
        var language = Safe("LanguageGuesser", () => GuessLanguage(text, languageMode));
        if (language is not null) diag.Append(" lingua=").Append(language);

        return new ReadOutcome(candidate.Source, text, candidate.Kind, language, candidate.Sensitive, (int)stopwatch.ElapsedMilliseconds, diag.ToString());
    }

    /// <summary>Caratteri di controllo (Word: \r, \a, \v, \f, U+FFFC) e tabulazioni diventano spazi o a capo; gli spazi multipli si riducono a uno.</summary>
    internal static string NormalizeWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false, pendingNewLine = false;
        foreach (var c in text)
        {
            bool newLine = c is '\n' or '\r' or '\v' or '\f' or '\u2028' or '\u2029';
            bool space = !newLine && (char.IsWhiteSpace(c) || char.IsControl(c) || c is '\uFFFC' or '\u200B' or '\u00A0');
            if (newLine) { pendingNewLine = true; continue; }
            if (space) { pendingSpace = true; continue; }
            if (sb.Length > 0)
            {
                if (pendingNewLine) sb.Append('\n');
                else if (pendingSpace) sb.Append(' ');
            }
            pendingSpace = pendingNewLine = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Tronca a <paramref name="max"/> caratteri cercando di fermarsi a fine parola (non prima della metà).</summary>
    internal static string TruncateAtWordBoundary(string text, int max)
    {
        if (max <= 0 || text.Length <= max) return text;
        int cut = max;
        for (int i = max; i > max / 2; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                cut = i;
                break;
            }
        }
        return text[..cut].TrimEnd();
    }

    // ---------------------------------------------------------------------------------------------
    // Protezioni
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Esegue una fase asincrona con tempo massimo. Scaduto il tempo la fase vale "nessun risultato" anche se il Task sottostante
    /// non onora l'annullamento; l'annullamento della richiesta intera viene invece propagato.
    /// </summary>
    private async Task<T?> GuardAsync<T>(string stage, Func<CancellationToken, Task<T?>> action, TimeSpan timeout, StringBuilder diag, CancellationToken ct) where T : class
    {
        var sw = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        Task<T?> task;
        try
        {
            task = action(linked.Token) ?? Task.FromResult<T?>(null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogStageFailure(stage, ex);
            diag.Append(' ').Append(stage).Append(":errore");
            return null;
        }

        try
        {
            var completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)).ConfigureAwait(false);
            if (completed != task)
            {
                Observe(task);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                _log.Warn($"Fase '{stage}' oltre il tempo massimo di {timeout.TotalMilliseconds:0} ms");
                diag.Append(' ').Append(stage).Append(":tempo-scaduto");
                return null;
            }
            var result = await task.ConfigureAwait(false);
            diag.Append(' ').Append(stage).Append(result is null ? ":no(" : ":ok(").Append(sw.ElapsedMilliseconds).Append("ms)");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            _log.Warn($"Fase '{stage}' oltre il tempo massimo di {timeout.TotalMilliseconds:0} ms");
            diag.Append(' ').Append(stage).Append(":tempo-scaduto");
            return null;
        }
        catch (Exception ex)
        {
            LogStageFailure(stage, ex);
            diag.Append(' ').Append(stage).Append(":errore");
            return null;
        }
    }

    /// <summary>Chiamata sincrona protetta: eccezioni registrate e trasformate in "nessun risultato".</summary>
    private T? Safe<T>(string stage, Func<T?> action) where T : class
    {
        try { return action(); }
        catch (Exception ex)
        {
            LogStageFailure(stage, ex);
            return null;
        }
    }

    private T Safe<T>(string stage, Func<T> action, T fallback) where T : struct
    {
        try { return action(); }
        catch (Exception ex)
        {
            LogStageFailure(stage, ex);
            return fallback;
        }
    }

    private void LogStageFailure(string stage, Exception ex)
    {
        if (ex is NotImplementedException) _log.Warn($"Fase '{stage}' non ancora implementata: trattata come nessun testo");
        else _log.Error($"Fase '{stage}' fallita", ex);
    }

    private static void Observe(Task task)
    {
        // Evita eccezioni non osservate di un Task che finirà più tardi.
        _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    private bool IsAvailable(IOcrEngine engine)
    {
        try { return engine.IsAvailable; }
        catch (Exception ex)
        {
            _log.Error($"Il motore OCR '{SafeName(engine)}' non risponde a IsAvailable", ex);
            return false;
        }
    }

    private static string SafeName(IOcrEngine engine)
    {
        try { return engine.Name ?? engine.GetType().Name; }
        catch { return engine.GetType().Name; }
    }

    // ---------------------------------------------------------------------------------------------
    // Geometria
    // ---------------------------------------------------------------------------------------------

    internal static bool IsOcrOnlyProcess(string? processName, ReadingSettings reading)
    {
        if (string.IsNullOrWhiteSpace(processName) || reading.OcrOnlyProcesses is not { Count: > 0 }) return false;
        var name = StripExe(processName.Trim());
        foreach (var entry in reading.OcrOnlyProcesses)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (string.Equals(name, StripExe(entry.Trim()), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;

        static string StripExe(string s) => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
    }

    internal static bool IsSmallElement(ScreenRect bounds, double dpi) =>
        bounds.Width <= SmallElementWidth * dpi && bounds.Height <= SmallElementHeight * dpi;

    /// <summary>Coordinate del puntatore nello spazio dell'immagine catturata (con eventuale scala fra rettangolo di schermo e pixel dell'immagine).</summary>
    internal static (double X, double Y) ToImageSpace(CapturedImage image, ScreenPoint point)
    {
        var (sx, sy) = Scale(image);
        return ((point.X - image.ScreenBounds.X) * sx, (point.Y - image.ScreenBounds.Y) * sy);
    }

    internal static ImageRect ToImageRect(CapturedImage image, ScreenRect rect)
    {
        var (sx, sy) = Scale(image);
        return new ImageRect((rect.X - image.ScreenBounds.X) * sx, (rect.Y - image.ScreenBounds.Y) * sy, rect.Width * sx, rect.Height * sy);
    }

    private static (double X, double Y) Scale(CapturedImage image)
    {
        var sb = image.ScreenBounds;
        double sx = sb.Width > 0 ? image.Width / (double)sb.Width : 1.0;
        double sy = sb.Height > 0 ? image.Height / (double)sb.Height : 1.0;
        return (sx, sy);
    }

    private sealed record Candidate(ReadSource Source, string Text, SpeechKind Kind, bool Sensitive);
}
