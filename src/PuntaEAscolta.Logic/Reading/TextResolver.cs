using System.Diagnostics;
using System.Globalization;
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
/// La richiesta della selezione cerca qualunque selezione, poi gli appunti, e senza selezione passa al flusso del puntatore.
/// Ogni fase è protetta singolarmente da eccezioni, tempo massimo e annullamento: un guasto fa passare alla fase successiva.
/// Non attiva finestre, non tocca il focus, non registra il testo se non a livello Debug.
/// </summary>
public sealed class TextResolver : ITextResolver
{
    /// <summary>Margine aggiunto al rettangolo di un suggerimento senza testo prima dell'OCR.</summary>
    public const int TooltipInflatePx = 8;

    /// <summary>
    /// Posizione di un vero suggerimento rispetto al puntatore (pixel al 100%, moltiplicati per la scala del monitor): il bordo
    /// superiore fra 10 px sopra e 100 px sotto il puntatore, il bordo sinistro non oltre 60 px a destra e il bordo destro non
    /// oltre 300 px a sinistra. Una finestra piccola altrove (menu a tendina, popup di WPF, pannelli di Affinity) non è il
    /// suggerimento del punto: si passa all'OCR della zona.
    /// </summary>
    public const int TooltipMaxAbovePx = 10;
    public const int TooltipMaxBelowPx = 100;
    public const int TooltipMaxRightOfPointerPx = 60;
    public const int TooltipMaxLeftOfPointerPx = 300;

    /// <summary>
    /// Tempo massimo predefinito per ogni chiamata all'accessibilità. Il cane da guardia di UiaTextSource (1400 ms) deve
    /// restare sotto questo valore, così una fase scaduta ha già liberato il thread UIA prima che parta la successiva.
    /// </summary>
    public const int DefaultUiaTimeoutMs = 1500;

    /// <summary>
    /// Tempo massimo predefinito per il ripiego sugli appunti. Regola da rispettare (la verifica ClipboardTimeoutBudgetTests):
    /// attesa del rilascio dei modificatori (3000 ms, InputInjection.ModifierReleaseTimeoutMs) + attesa della copia (300 ms)
    /// + aperture degli appunti con i nuovi tentativi + margine devono restare sotto questo valore, altrimenti chi tiene
    /// premuti i tasti della scorciatoia un po' più a lungo vedrebbe scadere la fase prima del Ctrl+C.
    /// </summary>
    public const int DefaultClipboardTimeoutMs = 4500;

    /// <summary>Dimensioni (al 100%) sotto le quali un elemento UIA senza testo delimita l'OCR al suo rettangolo.</summary>
    public const int SmallElementWidth = 300;
    public const int SmallElementHeight = 120;

    /// <summary>Una riga OCR fino a questa lunghezza è pronunciata come etichetta, oltre come frase.</summary>
    public const int MaxLabelChars = 60;

    /// <summary>Distanza (pixel immagine) entro la quale una parola OCR "tocca" il bordo della cattura.</summary>
    internal const double CutEdgeTolerancePx = 2.0;

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
    internal TimeSpan UiaTimeout { get; set; } = TimeSpan.FromMilliseconds(DefaultUiaTimeoutMs);

    /// <summary>Tempo massimo per ogni riconoscimento OCR.</summary>
    internal TimeSpan OcrTimeout { get; set; } = TimeSpan.FromMilliseconds(3000);

    /// <summary>Tempo massimo per il ripiego sugli appunti (vedi <see cref="DefaultClipboardTimeoutMs"/>).</summary>
    internal TimeSpan ClipboardTimeout { get; set; } = TimeSpan.FromMilliseconds(DefaultClipboardTimeoutMs);

    /// <summary>Scarta le parole OCR tagliate dal bordo della zona catturata (frammenti come "mento" per "Documento").</summary>
    internal bool DiscardCutText { get; set; } = true;

    /// <summary>
    /// Rettangolo visibile della finestra di primo livello sotto il punto (pixel fisici), o null se non si conosce. Se c'è,
    /// la zona dell'OCR viene limitata a quella finestra: il testo di un'altra finestra che sta dietro non si legge (prove dal
    /// vivo, BUG A: Word in una finestra, area grigia, lette le righe della finestra dietro). Impostato dalla radice di
    /// composizione (sul Core congelato non c'è un contratto per questo); null nei test che non lo usano.
    /// </summary>
    public Func<ScreenPoint, ScreenRect?>? WindowBoundsAtPoint { get; set; }

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
                ReadRequestKind.Selection => await ResolveSelectionRequestAsync(request.Point, settings, diag, ct).ConfigureAwait(false),
                ReadRequestKind.ZoneAroundPointer => await OcrZoneAsync(request.Point, settings, wholeZone: true, element: null, clipToElement: false, diag, ct).ConfigureAwait(false),
                _ => await ResolveAtPointerAsync(request.Point, settings, diag, ct).ConfigureAwait(false),
            };
            var outcome = Finish(candidate, settings, stopwatch, diag);
            if (outcome.HasText)
            {
                _log.Info($"Testo trovato da {outcome.Source} in {outcome.ElapsedMs} ms ({outcome.Text.Length} caratteri) | {outcome.Diagnostics}");
                if (_log.IsDebugEnabled) _log.Debug($"Testo: \"{outcome.Text}\"");
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
            AppendTotal(diag, stopwatch);
            return ReadOutcome.Nothing((int)stopwatch.ElapsedMilliseconds, diag.ToString());
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Flussi
    // ---------------------------------------------------------------------------------------------

    /// <param name="selectionAlreadyChecked">
    /// Vero quando arriva dalla richiesta della selezione, che l'ha già cercata senza trovarla: non si chiede di nuovo.
    /// </param>
    private async Task<Candidate?> ResolveAtPointerAsync(ScreenPoint point, AppSettings settings, StringBuilder diag, CancellationToken ct,
        bool selectionAlreadyChecked = false)
    {
        // 1. Selezione: si legge solo se il puntatore è dentro uno dei rettangoli del testo selezionato.
        if (settings.Reading.ReadSelectionWhenPointerInside && !selectionAlreadyChecked)
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

                // Barra di scorrimento (o il suo cursore) senza nome: controllo noto, meglio il silenzio che l'OCR della riga vicina
                // (Word: "Accessibilità: conforme" della barra di stato sotto la barra orizzontale).
                if (element.Kind == UiElementKind.ScrollBar || element.ParentKind == UiElementKind.ScrollBar)
                {
                    diag.Append(" barra-di-scorrimento");
                    return null;
                }
            }
        }
        ct.ThrowIfCancellationRequested();

        // 3. Suggerimento visibile vicino al puntatore, solo se è messo dove si mette un suggerimento.
        var tooltip = await GuardAsync("suggerimento", t => _ui.FindTooltipAsync(point, t), UiaTimeout, diag, ct).ConfigureAwait(false);
        if (tooltip is not null && !IsPlacedLikeTooltip(tooltip.Bounds, point, SafeDpi(point)))
        {
            diag.Append(" suggerimento:lontano");
            tooltip = null;
        }
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

    /// <summary>
    /// "Leggi la selezione": qualunque selezione (accessibilità), poi il ripiego sugli appunti; se non c'è niente di
    /// selezionato (selezione vuota e appunti vuoti o rifiutati) legge ciò che è sotto il puntatore, come "leggi sotto il
    /// puntatore". Così una sola scorciatoia fa "leggi ciò che ho selezionato, altrimenti ciò che indico" (prove dal vivo del
    /// 23/09/2026: con il solo touchpad questa era l'unica scorciatoia e le etichette dei menu non si leggevano mai).
    /// </summary>
    private async Task<Candidate?> ResolveSelectionRequestAsync(ScreenPoint point, AppSettings settings, StringBuilder diag, CancellationToken ct)
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
        }
        else
        {
            var copied = await GuardAsync("appunti", t => _clipboard.TryCopySelectionAsync(t), ClipboardTimeout, diag, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(copied))
            {
                diag.Append(" -> ClipboardSelection");
                return new Candidate(ReadSource.ClipboardSelection, copied, SpeechKind.Sentence, Sensitive: true);
            }
            ct.ThrowIfCancellationRequested();
        }

        diag.Append(" selezione:nessuna -> puntatore");
        return await ResolveAtPointerAsync(point, settings, diag, ct, selectionAlreadyChecked: true).ConfigureAwait(false);
    }

    private async Task<Candidate?> OcrTooltipAsync(ScreenRect bounds, ScreenPoint anchor, AppSettings settings, StringBuilder diag, CancellationToken ct)
    {
        var rect = bounds.Inflate(TooltipInflatePx, TooltipInflatePx);
        var image = Safe("cattura suggerimento", () => _capture.Capture(rect, anchor));
        if (!IsUsable(image))
        {
            diag.Append(" cattura-suggerimento:fallita");
            return null;
        }
        diag.Append(" ocr-suggerimento=").Append(image!.Width).Append('x').Append(image.Height);
        // Il margine di 8 px garantisce che il testo del suggerimento non tocchi il bordo: niente filtro dei tagli.
        var candidate = await RunOcrAsync(image, image.Width / 2.0, image.Height / 2.0, settings, wholeZone: true, clipTo: null, UiElementKind.ToolTip, desired: null, diag, ct).ConfigureAwait(false);
        if (candidate is null) return null;
        diag.Append(" -> TooltipOcr");
        // Un suggerimento breve è un'etichetta (cache e modello veloce), anche se letto come zona intera.
        var kind = candidate.Text.Length <= MaxLabelChars ? SpeechKind.Label : SpeechKind.Sentence;
        return candidate with { Source = ReadSource.TooltipOcr, Kind = kind };
    }

    /// <summary>Scala del monitor sotto il punto; 1.0 se la piattaforma non risponde o restituisce un valore senza senso.</summary>
    private double SafeDpi(ScreenPoint point)
    {
        double dpi = Safe("scala DPI", () => _capture.GetDpiScale(point), fallback: 1.0);
        return dpi <= 0 || double.IsNaN(dpi) || double.IsInfinity(dpi) ? 1.0 : dpi;
    }

    /// <summary>
    /// Vero se il rettangolo è dove Windows e le app mettono un suggerimento per il punto: bordo superiore fra
    /// <see cref="TooltipMaxAbovePx"/> sopra e <see cref="TooltipMaxBelowPx"/> sotto il puntatore, bordo sinistro al massimo
    /// <see cref="TooltipMaxRightOfPointerPx"/> a destra e bordo destro al massimo <see cref="TooltipMaxLeftOfPointerPx"/> a
    /// sinistra (valori al 100%, scalati). Un rettangolo vuoto non si può verificare: non è un suggerimento.
    /// La stessa regola (copiata in <c>ElementReader.IsPlacedLikeTooltip</c>, un test le confronta) filtra già le finestre
    /// candidate nello strato UIA; qui resta come controllo per qualunque sorgente.
    /// </summary>
    internal static bool IsPlacedLikeTooltip(ScreenRect bounds, ScreenPoint point, double dpi)
    {
        if (bounds.IsEmpty) return false;
        if (dpi <= 0 || double.IsNaN(dpi) || double.IsInfinity(dpi)) dpi = 1.0;
        bool vertical = bounds.Y >= point.Y - TooltipMaxAbovePx * dpi && bounds.Y <= point.Y + TooltipMaxBelowPx * dpi;
        bool horizontal = bounds.X <= point.X + TooltipMaxRightOfPointerPx * dpi && bounds.Right >= point.X - TooltipMaxLeftOfPointerPx * dpi;
        return vertical && horizontal;
    }

    private async Task<Candidate?> OcrZoneAsync(ScreenPoint point, AppSettings settings, bool wholeZone, UiElementInfo? element, bool clipToElement, StringBuilder diag, CancellationToken ct)
    {
        double dpi = SafeDpi(point);

        var zone = ZoneAround(point, settings.Ocr, dpi);
        // Solo la finestra sotto il puntatore. I lati tolti così non contano come tagli (DropCutText riceve la zona intera):
        // lì finisce la finestra, come al bordo del monitor.
        var request = zone;
        if (WindowBoundsAtPoint is { } windowAt)
        {
            ScreenRect? window = null;
            try { window = windowAt(point); }
            catch (Exception ex) { LogStageFailure("finestra sotto il punto", ex); }
            request = ClipZoneToWindow(zone, point, window);
            if (request != zone) diag.Append(" finestra=").Append(request.Width).Append('x').Append(request.Height);
        }
        var image = Safe("cattura zona", () => _capture.Capture(request, point));
        if (!IsUsable(image))
        {
            diag.Append(" cattura:fallita");
            return null;
        }
        diag.Append(" zona=").Append(image!.Width).Append('x').Append(image.Height).Append(" dpi=").Append(dpi.ToString("0.##", CultureInfo.InvariantCulture));

        var (px, py) = ToImageSpace(image, point);
        ImageRect? clip = null;
        if (clipToElement && element is not null && !element.Bounds.IsEmpty && IsSmallElement(element.Bounds, dpi))
        {
            clip = ToImageRect(image, element.Bounds);
            diag.Append(" ritaglio-elemento");
        }

        return await RunOcrAsync(image, px, py, settings, wholeZone, clip, element?.Kind, zone, diag, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Riconoscimento con scelta del motore (DESIGN.md 3.1 parte B).
    /// Auto: primario (Windows); se non trova nulla vicino al puntatore e il motore sa farlo, passaggio mirato attorno al punto
    /// (<see cref="IPointOcrEngine"/>, recupera il testo a basso contrasto); poi secondario (ONNX) se ancora nulla, oppure se
    /// l'elemento è un'immagine o un'area di disegno e il primario ha trovato meno di due righe.
    /// WindowsOnly: solo il primario. OnnxOnly: solo il secondario (il primario se ONNX non è disponibile).
    /// </summary>
    /// <param name="desired">Rettangolo richiesto alla cattura: serve a capire quali bordi tagliano il testo. Null = nessun filtro.</param>
    private async Task<Candidate?> RunOcrAsync(CapturedImage image, double px, double py, AppSettings settings, bool wholeZone, ImageRect? clipTo, UiElementKind? elementKind, ScreenRect? desired, StringBuilder diag, CancellationToken ct)
    {
        var mode = settings.Ocr.Mode;
        IOcrEngine? primary = IsAvailable(_primaryOcr) ? _primaryOcr : null;
        IOcrEngine? secondary = _secondaryOcr is not null && IsAvailable(_secondaryOcr) ? _secondaryOcr : null;

        IOcrEngine? first;
        IOcrEngine? fallback;
        switch (mode)
        {
            case OcrMode.WindowsOnly:
                first = primary;
                fallback = null;
                break;
            case OcrMode.OnnxOnly:
                first = secondary ?? primary;
                fallback = null;
                if (secondary is null && primary is not null) diag.Append(" (ONNX non disponibile: uso il motore primario)");
                break;
            default:
                first = primary ?? secondary;
                fallback = primary is not null ? secondary : null;
                break;
        }

        if (first is null)
        {
            diag.Append(" ocr:nessun-motore");
            _log.Warn("Nessun motore OCR disponibile");
            return null;
        }

        // Primo motore, passaggio normale.
        var firstResult = await RecognizeAsync(first, image, desired, px, py, diag, ct).ConfigureAwait(false);
        int firstLines = firstResult?.Lines.Count ?? 0;
        var best = Select(firstResult, px, py, settings, wholeZone, clipTo);
        ct.ThrowIfCancellationRequested();

        // Passaggio mirato attorno al punto: solo se il passaggio normale è riuscito ma non ha trovato nulla vicino al puntatore.
        if (best is null && firstResult is not null && first is IPointOcrEngine pointEngine)
        {
            var pointResult = await GuardAsync(
                "ocr-mirato:" + SafeName(first),
                async t => (OcrResult?)await pointEngine.RecognizeAroundPointAsync(image, px, py, t).ConfigureAwait(false),
                OcrTimeout, diag, ct).ConfigureAwait(false);
            if (pointResult is not null)
            {
                pointResult = Normalize(pointResult);
                if (DiscardCutText && desired is { } d) pointResult = DropCutText(pointResult, image, d, px, py, diag);
                diag.Append(" righe=").Append(pointResult.Lines.Count);
                firstLines = Math.Max(firstLines, pointResult.Lines.Count);
                best = Select(pointResult, px, py, settings, wholeZone, clipTo);
                if (best is not null) diag.Append(" scelto=mirato");
            }
            ct.ThrowIfCancellationRequested();
        }

        // Secondo motore (solo in Auto).
        bool imageLike = elementKind is UiElementKind.Image or UiElementKind.Pane or UiElementKind.Custom or UiElementKind.Document;
        bool useFallback = fallback is not null && (best is null || (imageLike && firstLines < 2));
        if (useFallback)
        {
            // Interfaccia (menu, pannelli): il passaggio mirato del secondo motore costa 100-180 ms contro 1,4-1,5 s del
            // passaggio completo su una zona densa (docs/impl-notes/ocr-onnx.md). Immagini e cartelli: passaggio completo,
            // perché il blocco di testo può estendersi oltre la finestra mirata.
            OcrResult? fallbackResult;
            if (!wholeZone && !imageLike && fallback is IPointOcrEngine fallbackPoint)
            {
                fallbackResult = await GuardAsync(
                    "ocr-mirato:" + SafeName(fallback),
                    async t => (OcrResult?)await fallbackPoint.RecognizeAroundPointAsync(image, px, py, t).ConfigureAwait(false),
                    OcrTimeout, diag, ct).ConfigureAwait(false);
                if (fallbackResult is not null)
                {
                    fallbackResult = Normalize(fallbackResult);
                    if (DiscardCutText && desired is { } d) fallbackResult = DropCutText(fallbackResult, image, d, px, py, diag);
                    diag.Append(" righe=").Append(fallbackResult.Lines.Count);
                }
            }
            else
            {
                fallbackResult = await RecognizeAsync(fallback!, image, desired, px, py, diag, ct).ConfigureAwait(false);
            }
            var fromFallback = Select(fallbackResult, px, py, settings, wholeZone, clipTo);
            if (fromFallback is not null)
            {
                best = fromFallback;
                diag.Append(" scelto=").Append(SafeName(fallback!));
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

    private async Task<OcrResult?> RecognizeAsync(IOcrEngine engine, CapturedImage image, ScreenRect? desired, double px, double py, StringBuilder diag, CancellationToken ct)
    {
        var result = await GuardAsync(
            "ocr:" + SafeName(engine),
            async t => (OcrResult?)await engine.RecognizeAsync(image, t).ConfigureAwait(false),
            OcrTimeout, diag, ct).ConfigureAwait(false);
        if (result is null) return null;
        result = Normalize(result);
        if (DiscardCutText && desired is { } d) result = DropCutText(result, image, d, px, py, diag);
        diag.Append(" righe=").Append(result.Lines.Count);
        return result;
    }

    private OcrSelection? Select(OcrResult? result, double px, double py, AppSettings settings, bool wholeZone, ImageRect? clipTo)
    {
        if (result is null || result.Lines.Count == 0) return null;
        var selection = Safe("PointerTextSelector", () => SelectOcrText(result, px, py, settings.Ocr, wholeZone, clipTo));
        return selection is null || string.IsNullOrWhiteSpace(selection.Text) ? null : selection;
    }

    /// <summary>Un motore che restituisce Lines null o righe null non deve far cadere la lettura.</summary>
    private static OcrResult Normalize(OcrResult result)
    {
        if (result.Lines is null) return result with { Lines = Array.Empty<OcrLine>() };
        if (result.Lines.Any(l => l is null)) return result with { Lines = result.Lines.Where(l => l is not null).ToList() };
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Testo tagliato dal bordo della cattura
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Toglie il testo tagliato dal bordo della zona catturata (docs/impl-notes/windows-ocr.md, "Limiti").
    /// Un bordo "taglia" solo se coincide con il rettangolo richiesto: se la cattura è stata ristretta dal bordo del monitor,
    /// quel lato è la fine dello schermo e il testo lì è intero.
    /// Bordi sinistro e destro: si tolgono le parole che li toccano (non quella sotto il puntatore); la riga resta con le altre.
    /// Bordi superiore e inferiore: si toglie la riga che li tocca, a meno che contenga il puntatore.
    /// </summary>
    internal static OcrResult DropCutText(OcrResult result, CapturedImage image, ScreenRect desired, double px, double py, StringBuilder? diag = null)
    {
        if (result.Lines.Count == 0 || image.Width <= 0 || image.Height <= 0) return result;

        var sb = image.ScreenBounds;
        bool cutLeft = sb.X <= desired.X;
        bool cutRight = sb.Right >= desired.Right;
        bool cutTop = sb.Y <= desired.Y;
        bool cutBottom = sb.Bottom >= desired.Bottom;
        if (!cutLeft && !cutRight && !cutTop && !cutBottom) return result;

        double tol = CutEdgeTolerancePx;
        double maxX = image.Width - tol;
        double maxY = image.Height - tol;
        int removedWords = 0, removedLines = 0;
        var kept = new List<OcrLine>(result.Lines.Count);

        foreach (var line in result.Lines)
        {
            var box = line.Box;
            bool pointerInRow = py >= box.Y && py <= box.Bottom;
            if (!pointerInRow && ((cutTop && box.Y <= tol) || (cutBottom && box.Bottom >= maxY)))
            {
                removedLines++;
                continue;
            }

            bool touchesSide = (cutLeft && box.X <= tol) || (cutRight && box.Right >= maxX);
            if (!touchesSide)
            {
                kept.Add(line);
                continue;
            }

            if (line.Words is not { Count: > 0 })
            {
                // Senza parole non si può ritagliare: si scarta la riga solo se non passa sotto il puntatore.
                if (px >= box.X && px <= box.Right) kept.Add(line);
                else removedLines++;
                continue;
            }

            var words = new List<OcrWord>(line.Words.Count);
            foreach (var word in line.Words)
            {
                if (word is null) continue;
                var wb = word.Box;
                bool underPointer = px >= wb.X && px <= wb.Right && pointerInRow;
                bool cut = (cutLeft && wb.X <= tol) || (cutRight && wb.Right >= maxX);
                if (cut && !underPointer) { removedWords++; continue; }
                words.Add(word);
            }

            if (words.Count == line.Words.Count)
            {
                kept.Add(line);
                continue;
            }
            if (words.Count == 0)
            {
                removedLines++;
                continue;
            }

            double left = words.Min(w => w.Box.X);
            double right = words.Max(w => w.Box.Right);
            var text = string.Join(' ', words.Select(w => w.Text));
            kept.Add(new OcrLine(text, new ImageRect(left, box.Y, right - left, box.Height), words, line.Confidence));
        }

        if (removedWords == 0 && removedLines == 0) return result;
        diag?.Append(" tagliati=").Append(removedWords).Append('p').Append(removedLines).Append('r');
        return result with { Lines = kept };
    }

    // ---------------------------------------------------------------------------------------------
    // Rifinitura del testo
    // ---------------------------------------------------------------------------------------------

    private ReadOutcome Finish(Candidate? candidate, AppSettings settings, Stopwatch stopwatch, StringBuilder diag)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Text))
        {
            AppendTotal(diag, stopwatch);
            return ReadOutcome.Nothing((int)stopwatch.ElapsedMilliseconds, diag.ToString());
        }

        var text = NormalizeWhitespace(candidate.Text);

        if (settings.Reading.StripEmoji)
        {
            var stripped = Safe("EmojiFilter", () => StripEmoji(text));
            if (stripped is not null) text = NormalizeWhitespace(stripped);
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            diag.Append(" solo-emoji");
            AppendTotal(diag, stopwatch);
            return ReadOutcome.Nothing((int)stopwatch.ElapsedMilliseconds, diag.ToString());
        }

        int max = settings.Reading.MaxCharsPerRead;
        if (max > 0 && text.Length > max)
        {
            text = TruncateAtWordBoundary(text, max);
            diag.Append(" troncato=").Append(text.Length);
        }

        // La lingua forzata dalle impostazioni vale per le etichette; frasi e blocchi sono sempre riconosciuti dal testo.
        var languageMode = candidate.Kind == SpeechKind.Label ? settings.Speech.LabelLanguage : LabelLanguageMode.Auto;
        var language = Safe("LanguageGuesser", () => GuessLanguage(text, languageMode));
        if (language is not null) diag.Append(" lingua=").Append(language);

        AppendTotal(diag, stopwatch);
        return new ReadOutcome(candidate.Source, text, candidate.Kind, language, candidate.Sensitive, (int)stopwatch.ElapsedMilliseconds, diag.ToString());
    }

    private static void AppendTotal(StringBuilder diag, Stopwatch stopwatch) =>
        diag.Append(" totale=").Append(stopwatch.ElapsedMilliseconds).Append("ms");

    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;
    private const char ObjectReplacement = (char)0xFFFC;
    private const char ZeroWidthSpace = (char)0x200B;
    private const char NoBreakSpace = (char)0x00A0;

    /// <summary>Caratteri di controllo (Word: CR, BEL, VT, FF, sostituto di oggetto) e tabulazioni diventano spazi o a capo; gli spazi multipli si riducono a uno.</summary>
    internal static string NormalizeWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false, pendingNewLine = false;
        foreach (var c in text)
        {
            bool newLine = c is '\n' or '\r' or '\v' or '\f' or LineSeparator or ParagraphSeparator;
            bool space = !newLine && (char.IsWhiteSpace(c) || char.IsControl(c) || c is ObjectReplacement or ZeroWidthSpace or NoBreakSpace);
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
    /// non onora l'annullamento (non lo si aspetta); l'annullamento della richiesta intera viene invece propagato subito.
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
            var result = await task.WaitAsync(timeout, ct).ConfigureAwait(false);
            diag.Append(' ').Append(stage).Append(result is null ? ":no(" : ":ok(").Append(sw.ElapsedMilliseconds).Append("ms)");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Il token della fase va annullato esplicitamente. Quando Cancel arriva dal worker (nessun contesto di
            // sincronizzazione) le registrazioni su ct girano dall'ultima alla prima: quella di WaitAsync riprende questo
            // metodo dentro Cancel stesso, e chiudere il linked (using) toglierebbe la sua registrazione prima che scatti.
            // La fase (OCR ONNX, UIA) continuerebbe a lavorare per niente fino in fondo (prove dal vivo, 230-380 ms di CPU).
            try { linked.Cancel(); }
            catch (AggregateException) { /* errori nei gestori di annullamento della fase: la fase è comunque abbandonata */ }
            Observe(task);
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // TimeoutException: la fase non ha risposto in tempo; OperationCanceledException: la fase ha onorato il proprio tempo massimo.
            // Se WaitAsync scade prima del CancelAfter, il token della fase va annullato qui: chiudere il linked (using) toglierebbe
            // il timer e la fase (per esempio un OCR che onora l'annullamento) continuerebbe a lavorare per niente.
            try { linked.Cancel(); }
            catch (AggregateException) { /* errori nei gestori di annullamento della fase: la fase è comunque abbandonata */ }
            Observe(task);
            _log.Warn($"Fase '{stage}' oltre il tempo massimo di {timeout.TotalMilliseconds:0} ms");
            diag.Append(' ').Append(stage).Append(":tempo-scaduto(").Append(sw.ElapsedMilliseconds).Append("ms)");
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
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

    private static bool IsUsable(CapturedImage? image) =>
        image is not null && image.Width > 0 && image.Height > 0 && image.Bgra is not null;

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

    /// <summary>Zona di cattura: ZoneWidth x ZoneHeight (al 100%) moltiplicate per la scala del monitor, centrata sul puntatore.</summary>
    internal static ScreenRect ZoneAround(ScreenPoint point, OcrSettings ocr, double dpi)
    {
        int width = Math.Max(16, (int)Math.Round(ocr.ZoneWidth * dpi));
        int height = Math.Max(16, (int)Math.Round(ocr.ZoneHeight * dpi));
        return ScreenRect.Around(point, width, height);
    }

    /// <summary>
    /// Zona limitata alla finestra sotto il puntatore. La zona resta intera se la finestra non si conosce, è vuota o non
    /// contiene il puntatore (bordo invisibile di ridimensionamento), o se l'intersezione è vuota.
    /// </summary>
    internal static ScreenRect ClipZoneToWindow(ScreenRect zone, ScreenPoint point, ScreenRect? window)
    {
        if (window is not { IsEmpty: false } w || !w.Contains(point)) return zone;
        var clipped = zone.Intersect(w);
        return clipped.IsEmpty ? zone : clipped;
    }

    /// <summary>Elemento "piccolo" (sotto 300x120 px al 100%, scalati): senza testo, il suo rettangolo delimita l'OCR.</summary>
    internal static bool IsSmallElement(ScreenRect bounds, double dpi) =>
        bounds.Width < SmallElementWidth * dpi && bounds.Height < SmallElementHeight * dpi;

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
