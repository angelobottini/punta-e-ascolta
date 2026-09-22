using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using UIA = Interop.UIAutomationClient;

namespace PuntaEAscolta.Windows.Automation;

/// <summary>
/// Logica di raccolta eseguita SUL THREAD UIA: elemento sotto il punto (con normalizzazione), contesto di testo
/// tramite TextPattern, selezione dell'elemento con il focus, ricerca dei tooltip. Non decide che cosa leggere.
/// </summary>
internal sealed partial class ElementReader
{
    /// <summary>Tetto di caratteri per GetText su paragrafo e selezione (mai DocumentRange / GetText(-1)).</summary>
    public const int MaxChars = 8000;

    /// <summary>Tolleranza verticale rispetto alla riga puntata, in altezze di riga (orizzontale: 1.0 h).</summary>
    public const double LineToleranceFactor = 0.6;

    // Word: 0 livelli; PDF: pochi livelli (1-2 ms ciascuno con la cache); browser: si passa dalla finestra Win32
    private const int MaxClimbForText = 6;
    private const int MaxClimbForSelection = 8;
    private const int SeparatorMaxHeightPx = 10;
    private const int MaxDescendantTexts = 12;
    private const int TooltipMaxDistancePx = 400;
    private const int TooltipMaxHeightPx = 120;
    private const int TooltipMaxWidthPx = 800;
    private const int TooltipMaxWindowsToQuery = 3;
    private const int HugeParagraphWindowLines = 6;
    private const int HugeParagraphMaxChars = 4000;
    private const int RetryDelayMs = 30;

    private readonly UiaSession _s;
    private readonly ILog _log;
    private readonly ProcessNameCache _processNames;

    public ElementReader(UiaSession session, ILog log, ProcessNameCache processNames)
    {
        _s = session;
        _log = log;
        _processNames = processNames;
    }

    // ------------------------------------------------------------------ elemento sotto il punto

    public UiElementInfo? GetElementAt(ScreenPoint point)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            if (NativeMethods.IsWindowAtPointHung(point.X, point.Y))
            {
                _log.Warn($"UIA: la finestra sotto il punto ({point.X},{point.Y}) non risponde ai messaggi; accessibilità saltata");
                return null;
            }

            var pt = new UIA.tagPOINT { x = point.X, y = point.Y };
            var hit = ElementFromPointWithRetry(pt);
            if (hit is null) return null;

            var hitSnap = ElementSnapshot.Read(hit);
            var (snap, name, parent) = Normalize(hitSnap);
            parent ??= GetParent(snap.Element);

            string? value = ReadValue(snap.Element);
            var toggle = ReadToggle(snap.Element);

            // LegacyIAccessible solo come ripiego quando manca il nome: sulle app Office raddoppia il costo (probe-office).
            string? legacyName = null, legacyDescription = null, legacyValue = null;
            if (name is null)
            {
                var legacy = ElementSnapshot.Safe(
                    () => snap.Element.GetCachedPattern(UiaIds.LegacyIAccessiblePattern) as UIA.IUIAutomationLegacyIAccessiblePattern, null);
                if (legacy is not null)
                {
                    legacyName = ElementSnapshot.Clean(ElementSnapshot.Safe(() => legacy.CurrentName, null));
                    legacyDescription = ElementSnapshot.Clean(ElementSnapshot.Safe(() => legacy.CurrentDescription, null));
                    legacyValue = ElementSnapshot.Clean(ElementSnapshot.Safe(() => legacy.CurrentValue, null));
                }
            }

            string? labeledBy = ReadLabeledByName(snap.LabeledBy);
            var text = TryReadTextContext(snap, point);
            string? processName = _processNames.Get(snap.ProcessId);
            int elapsed = (int)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            var info = new UiElementInfo
            {
                Kind = snap.Kind,
                Name = ElementSnapshot.Clean(name),
                Value = ElementSnapshot.Clean(value),
                HelpText = snap.HelpText,
                FullDescription = snap.FullDescription,
                LegacyName = legacyName,
                LegacyDescription = legacyDescription,
                LegacyValue = legacyValue,
                ItemStatus = snap.ItemStatus,
                AcceleratorKey = snap.AcceleratorKey,
                AccessKey = snap.AccessKey,
                LabeledByName = labeledBy,
                ToggleState = toggle,
                IsPassword = snap.IsPassword,
                IsEnabled = snap.IsEnabled,
                ClassName = snap.ClassName,
                FrameworkId = snap.FrameworkId,
                AutomationId = snap.AutomationId,
                ProcessName = processName,
                Bounds = snap.Bounds,
                ParentKind = parent?.Kind ?? UiElementKind.Unknown,
                ParentName = parent?.Name,
                Text = text,
                ElapsedMs = elapsed,
            };

            if (_log.IsDebugEnabled)
            {
                _log.Debug($"UIA: {info.Kind} nome='{info.Name}' classe={info.ClassName} framework={info.FrameworkId} " +
                           $"processo={info.ProcessName} genitore={info.ParentKind} testo={(text is null ? "no" : text.PointerOverText ? "sopra" : "fuori")} " +
                           $"colpito={hitSnap.Kind} {elapsed} ms");
            }
            return info;
        }
        catch (Exception ex)
        {
            LogFailure("GetElementAt", ex);
            return null;
        }
    }

    /// <summary>
    /// ElementFromPointBuildCache con un solo nuovo tentativo su UIA_E_ELEMENTNOTAVAILABLE: nelle pagine web in
    /// movimento (Chrome) il nodo colpito può sparire fra il colpo e la lettura delle proprietà.
    /// </summary>
    private UIA.IUIAutomationElement? ElementFromPointWithRetry(UIA.tagPOINT pt)
    {
        try
        {
            return _s.Automation.ElementFromPointBuildCache(pt, _s.ElementCache);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80040201))
        {
            _log.Debug("UIA: elemento sparito durante ElementFromPoint; nuovo tentativo");
            Thread.Sleep(RetryDelayMs);
            return _s.Automation.ElementFromPointBuildCache(pt, _s.ElementCache);
        }
    }

    /// <summary>
    /// Normalizzazione (probe Affinity): Text/Image dentro un controllo con etichetta -> il controllo; controllo con
    /// nome vuoto o nome di tipo .NET -> nome del Text discendente (anche fuori schermo); separatori -> nome null.
    /// </summary>
    private (ElementSnapshot Target, string? Name, ElementSnapshot? Parent) Normalize(ElementSnapshot hit)
    {
        var target = hit;
        string? name = hit.Name;
        ElementSnapshot? parent = null;

        if (hit.Kind is UiElementKind.Text or UiElementKind.Image)
        {
            var p = GetParent(hit.Element);
            if (p is not null && ControlTypeMap.IsLabelledControl(p.Kind))
            {
                target = p;
                name = p.Name;
                if (IsBadName(p.Name) && hit.Kind == UiElementKind.Text && hit.Name is not null) name = hit.Name;
            }
            else
            {
                parent = p; // già letto: riutilizzato come genitore dell'elemento colpito
            }
        }

        if (target.ControlTypeId == UiaIds.SeparatorControl) return (target, null, parent);
        if (target.Kind == UiElementKind.MenuItem && IsBadName(name)
            && target.Bounds.Height > 0 && target.Bounds.Height <= SeparatorMaxHeightPx)
        {
            return (target, null, parent); // separatore di menu travestito da MenuItem (Affinity)
        }

        if (IsBadName(name) && ControlTypeMap.IsLabelledControl(target.Kind))
        {
            var names = FindDescendantTextNames(target.Element);
            if (names.Count > 0)
            {
                name = names[0];
                if (names.Distinct(StringComparer.Ordinal).Skip(1).Any() && _log.IsDebugEnabled)
                    _log.Debug("UIA: i Text discendenti hanno nomi diversi; uso il primo");
            }
        }
        else if (IsBadName(name) && target.Kind == UiElementKind.ToolBar && IsIconSizedToolbar(target.Bounds))
        {
            // Affinity, barra superiore: ogni icona è una ToolBar a sé, senza nome, e FromPoint restituisce la ToolBar
            // stessa; il nome dell'icona sta nei Text figli (fuori schermo, rettangolo vuoto). Prove dal vivo, BUG 10.
            var names = FindDescendantTextNames(target.Element);
            string? iconName = IconToolbarName(names);
            if (iconName is not null) name = iconName;
            else if (names.Count > 0 && _log.IsDebugEnabled)
                _log.Debug($"UIA: ToolBar piccola con {names.Distinct(StringComparer.Ordinal).Count()} nomi diversi nei Text figli e nessun rettangolo per sceglierne uno; nessun nome");
        }

        return (target, name, parent);
    }

    /// <summary>Altezza massima (pixel fisici) di una ToolBar che è una sola icona: 34 px al 125% nella barra di Affinity, margine fino a circa il 280%.</summary>
    internal const int IconToolbarMaxHeightPx = 96;

    /// <summary>Rapporto massimo larghezza/altezza di una ToolBar che è una sola icona (Affinity: 46x34, 64x34 con la freccia della tendina).</summary>
    internal const double IconToolbarMaxAspect = 3.0;

    /// <summary>ToolBar grande quanto un'icona (non una barra intera con tanti pulsanti o lo spazio vuoto fra i gruppi).</summary>
    internal static bool IsIconSizedToolbar(ScreenRect bounds) =>
        !bounds.IsEmpty && bounds.Height <= IconToolbarMaxHeightPx && bounds.Width <= IconToolbarMaxAspect * bounds.Height;

    /// <summary>
    /// Nome di un'icona che è una ToolBar a sé: il nome comune a tutti i Text discendenti (Affinity lo ripete due volte). Con
    /// nomi diversi (la ToolBar di sinistra con "Vettore" e "Pixel": due icone e nessun rettangolo per sapere quale è sotto il
    /// puntatore) null: meglio il silenzio di un nome sbagliato. Senza nomi null.
    /// </summary>
    internal static string? IconToolbarName(IReadOnlyList<string> descendantTextNames)
    {
        string? single = null;
        foreach (var n in descendantTextNames)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            var trimmed = n.Trim();
            if (single is null) single = trimmed;
            else if (!string.Equals(single, trimmed, StringComparison.Ordinal)) return null;
        }
        return single;
    }

    /// <summary>Nomi non vuoti dei Text discendenti in ordine (vista raw: anche fuori schermo e con rettangolo vuoto), al massimo <see cref="MaxDescendantTexts"/>.</summary>
    private List<string> FindDescendantTextNames(UIA.IUIAutomationElement element)
    {
        var names = new List<string>();
        var found = ElementSnapshot.Safe(
            () => element.FindAllBuildCache(UIA.TreeScope.TreeScope_Descendants, _s.TextCondition, _s.ElementCache), null);
        if (found is null) return names;
        int count = ElementSnapshot.Safe(() => found.Length, 0);
        for (int i = 0; i < count && i < MaxDescendantTexts; i++)
        {
            var child = ElementSnapshot.Safe(() => found.GetElement(i), null);
            if (child is null) continue;
            string? n = ElementSnapshot.Clean(ElementSnapshot.Safe(() => child.CachedName, null));
            if (n is not null) names.Add(n);
        }
        return names;
    }

    private static bool IsBadName(string? name) => string.IsNullOrWhiteSpace(name) || TypeNameRegex().IsMatch(name);

    /// <summary>Nome di tipo .NET come Serif.Affinity.Workspaces.Workspace: almeno due punti, segmenti identificatori.</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*){2,}$")]
    private static partial Regex TypeNameRegex();

    private ElementSnapshot? GetParent(UIA.IUIAutomationElement element)
    {
        var p = ElementSnapshot.Safe(() => _s.RawWalker.GetParentElementBuildCache(element, _s.ElementCache), null);
        return p is null ? null : ElementSnapshot.Read(p);
    }

    /// <summary>
    /// Valore (ValuePattern) dalla cache; se la cache non lo contiene (elemento letto con un'altra richiesta, provider che
    /// rifiuta) si chiede il valore corrente, un solo giro in più e solo se il pattern esiste.
    /// </summary>
    internal static string? ReadValue(UIA.IUIAutomationElement element)
    {
        var pattern = ElementSnapshot.Safe(() => element.GetCachedPattern(UiaIds.ValuePattern) as UIA.IUIAutomationValuePattern, null);
        if (pattern is null) return null;
        try
        {
            return pattern.CachedValue;
        }
        catch (Exception)
        {
            return ElementSnapshot.Safe(() => pattern.CurrentValue, null);
        }
    }

    internal static UiToggleState ReadToggle(UIA.IUIAutomationElement element)
    {
        var toggle = ElementSnapshot.Safe(() => element.GetCachedPattern(UiaIds.TogglePattern) as UIA.IUIAutomationTogglePattern, null);
        if (toggle is null) return UiToggleState.None;
        var state = ElementSnapshot.Safe(() => (UIA.ToggleState?)toggle.CachedToggleState, null)
                    ?? ElementSnapshot.Safe(() => (UIA.ToggleState?)toggle.CurrentToggleState, null);
        return state switch
        {
            UIA.ToggleState.ToggleState_Off => UiToggleState.Off,
            UIA.ToggleState.ToggleState_On => UiToggleState.On,
            UIA.ToggleState.ToggleState_Indeterminate => UiToggleState.Indeterminate,
            _ => UiToggleState.None,
        };
    }

    private static string? ReadLabeledByName(UIA.IUIAutomationElement? labeledBy)
    {
        if (labeledBy is null) return null;
        string? cached = ElementSnapshot.Safe(() => labeledBy.CachedName, null);
        return ElementSnapshot.Clean(cached ?? ElementSnapshot.Safe(() => labeledBy.CurrentName, null));
    }

    // ------------------------------------------------------------------ TextPattern

    /// <summary>
    /// Contesto di testo protetto: le chiamate sugli intervalli (Clone, ExpandToEnclosingUnit, GetText...) possono fallire
    /// con COMException su una pagina che si ridisegna (Chromium) o un provider che rifiuta; allora l'elemento resta con
    /// nome e valore invece di perdere tutto e finire all'OCR. Se l'elemento ha il TextPattern il guasto vale "puntatore
    /// fuori dal testo" (come quando RangeFromPoint fallisce): per il risolutore Text = null significherebbe "elemento senza
    /// testo" e una pagina di Word o un'area di testo grande direbbe il proprio nome ("Contenuto pagina 1").
    /// </summary>
    private UiTextContext? TryReadTextContext(ElementSnapshot snap, ScreenPoint point)
    {
        try
        {
            return ReadTextContext(snap, point);
        }
        catch (Exception ex)
        {
            _log.Debug($"UIA: contesto di testo non leggibile ({ex.GetType().Name} 0x{ex.HResult:X8}); elemento tenuto senza testo");
            return TextContextAfterFailure(snap.HasTextPattern);
        }
    }

    /// <summary>Contesto da usare quando le chiamate sugli intervalli falliscono: "fuori dal testo" se l'elemento ha il TextPattern, altrimenti nessuno.</summary>
    internal static UiTextContext? TextContextAfterFailure(bool hasTextPattern) =>
        hasTextPattern ? new UiTextContext(string.Empty, 0, PointerOverText: false) : null;

    /// <summary>
    /// Paragrafo e offset sotto il punto (algoritmo validato 17/17 su Word): RangeFromPoint -> verifica sui rettangoli
    /// della riga -> paragrafo -> prefisso -> offset. Restituisce PointerOverText=false quando il punto non è sopra testo.
    /// </summary>
    private UiTextContext? ReadTextContext(ElementSnapshot snap, ScreenPoint point)
    {
        var host = FindTextHost(snap, point);
        if (host is null) return null;

        var tp = ElementSnapshot.Safe(() => host.Element.GetCachedPattern(UiaIds.TextPattern) as UIA.IUIAutomationTextPattern, null);
        if (tp is null) return null;

        var pt = new UIA.tagPOINT { x = point.X, y = point.Y };
        // intervallo degenere più VICINO al punto: torna anche su margini e aree vuote, quindi va verificato
        var caret = ElementSnapshot.Safe(() => tp.RangeFromPoint(pt), null);
        if (caret is null) return new UiTextContext(string.Empty, 0, false);

        var line = caret.Clone();
        line.ExpandToEnclosingUnit(UIA.TextUnit.TextUnit_Line);
        double[]? lineRects = ElementSnapshot.Safe(() => line.GetBoundingRectangles(), null);
        if (!IsNearLine(lineRects, point.X, point.Y, LineToleranceFactor)) return new UiTextContext(string.Empty, 0, false);

        var paragraph = caret.Clone();
        paragraph.ExpandToEnclosingUnit(UIA.TextUnit.TextUnit_Paragraph);

        var prefix = paragraph.Clone();
        prefix.MoveEndpointByRange(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, caret,
                                   UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
        string before = prefix.GetText(MaxChars) ?? string.Empty;
        string all = paragraph.GetText(MaxChars) ?? string.Empty;

        if (before.Length >= MaxChars)
        {
            // paragrafo enorme: finestra di +-6 RIGHE attorno al punto (10 ms; per caratteri costerebbe 145 ms)
            var window = caret.Clone();
            window.MoveEndpointByUnit(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, UIA.TextUnit.TextUnit_Line, -HugeParagraphWindowLines);
            window.MoveEndpointByUnit(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, UIA.TextUnit.TextUnit_Line, HugeParagraphWindowLines);
            var windowPrefix = window.Clone();
            windowPrefix.MoveEndpointByRange(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, caret,
                                             UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            before = windowPrefix.GetText(HugeParagraphMaxChars) ?? string.Empty;
            all = window.GetText(HugeParagraphMaxChars) ?? string.Empty;
        }

        if (all.Length == 0) return new UiTextContext(string.Empty, 0, false);
        int offset = Math.Min(before.Length, all.Length);
        if (_log.IsDebugEnabled) _log.Debug($"UIA: paragrafo di {all.Length} caratteri, offset {offset}");
        return new UiTextContext(all, offset, true);
    }

    /// <summary>
    /// L'elemento stesso se ospita testo (Word: 0 livelli); altrimenti il primo antenato ospite di testo entro pochi
    /// livelli (PDF); altrimenti, per gli alberi profondi dei browser, il Document figlio della finestra sotto il punto.
    /// </summary>
    private ElementSnapshot? FindTextHost(ElementSnapshot snap, ScreenPoint point)
    {
        if (snap.HasTextPattern && (ControlTypeMap.IsTextHostKind(snap.Kind) || snap.Kind == UiElementKind.Text)) return snap;
        if (!ControlTypeMap.MayClimbToTextHost(snap.Kind)) return null;

        var current = snap;
        for (int level = 0; level < MaxClimbForText; level++)
        {
            var p = GetParent(current.Element);
            if (p is null || p.Kind == UiElementKind.Window) break;
            if (p.HasTextPattern) return ControlTypeMap.IsTextHostKind(p.Kind) ? p : null;
            current = p;
        }
        return FindTextHostViaWindow(snap, point);
    }

    /// <summary>
    /// Chromium (Chrome, Edge, Electron): solo il Document di ogni frame ha il TextPattern e può stare a 10-20 livelli
    /// dal nodo colpito. La finestra Win32 sotto il punto (Chrome_RenderWidgetHostHWND) ha il Document radice come
    /// primo figlio con TextPattern; il suo RangeFromPoint funziona anche dentro gli iframe (misurato: 3-5 ms).
    /// </summary>
    private ElementSnapshot? FindTextHostViaWindow(ElementSnapshot snap, ScreenPoint point)
    {
        IntPtr hwnd = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = point.X, Y = point.Y });
        if (hwnd == IntPtr.Zero) return null;

        var root = ElementSnapshot.Safe(() => _s.Automation.ElementFromHandleBuildCache(hwnd, _s.ElementCache), null);
        if (root is null) return null;
        var rootSnap = ElementSnapshot.Read(root);
        if (rootSnap.ProcessId != snap.ProcessId) return null; // finestra di un altro processo: non è l'ospite del nodo colpito
        if (rootSnap.HasTextPattern && ControlTypeMap.IsTextHostKind(rootSnap.Kind)) return rootSnap;

        var doc = ElementSnapshot.Safe(
            () => root.FindFirstBuildCache(UIA.TreeScope.TreeScope_Children, _s.TextPatternCondition, _s.ElementCache), null);
        if (doc is null) return null;
        var docSnap = ElementSnapshot.Read(doc);
        return ControlTypeMap.IsTextHostKind(docSnap.Kind) ? docSnap : null;
    }

    /// <summary>True se il punto è sopra (o abbastanza vicino a) uno dei rettangoli della riga; tolleranza relativa all'altezza.</summary>
    internal static bool IsNearLine(double[]? rects, int x, int y, double toleranceLines)
    {
        if (rects is null) return false;
        for (int i = 0; i + 3 < rects.Length; i += 4)
        {
            double h = rects[i + 3];
            if (h <= 0 || double.IsNaN(h) || double.IsInfinity(h)) continue;
            double dx = Math.Max(Math.Max(rects[i] - x, x - (rects[i] + rects[i + 2])), 0);
            double dy = Math.Max(Math.Max(rects[i + 1] - y, y - (rects[i + 1] + h)), 0);
            if (dx <= h && dy <= toleranceLines * h) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ selezione

    public UiSelectionInfo? GetSelection()
    {
        try
        {
            // Prima fase di ogni clic: con l'app in primo piano bloccata, GetFocusedElement resterebbe appeso fino al
            // tempo massimo della transazione e le fasi successive aspetterebbero il thread UIA.
            if (NativeMethods.IsForegroundWindowHung())
            {
                _log.Warn("UIA: la finestra in primo piano non risponde ai messaggi; selezione saltata");
                return null;
            }

            var focused = _s.Automation.GetFocusedElementBuildCache(_s.ElementCache);
            if (focused is null) return null;

            var snap = ElementSnapshot.Read(focused);
            var host = snap.HasTextPattern ? snap : null;
            var current = snap;
            for (int level = 0; host is null && level < MaxClimbForSelection; level++)
            {
                var p = GetParent(current.Element);
                if (p is null) break;
                if (p.HasTextPattern) host = p;
                current = p;
            }
            if (host is null) return null;

            var tp = ElementSnapshot.Safe(() => host.Element.GetCachedPattern(UiaIds.TextPattern) as UIA.IUIAutomationTextPattern, null);
            if (tp is null) return null;

            var ranges = tp.GetSelection();
            if (ranges is null || ranges.Length == 0) return null;

            var sb = new StringBuilder();
            var rects = new List<ScreenRect>();
            for (int i = 0; i < ranges.Length; i++)
            {
                var range = ranges.GetElement(i);
                sb.Append(range.GetText(MaxChars));
                rects.AddRange(ElementSnapshot.ToScreenRects(ElementSnapshot.Safe(() => range.GetBoundingRectangles(), null)));
            }
            if (sb.Length == 0) return null; // solo il cursore: un intervallo degenere con testo vuoto

            if (_log.IsDebugEnabled) _log.Debug($"UIA: selezione di {sb.Length} caratteri in {rects.Count} rettangoli");
            return new UiSelectionInfo(sb.ToString(), rects);
        }
        catch (Exception ex)
        {
            LogFailure("GetSelection", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ tooltip

    public UiTooltipInfo? FindTooltip(ScreenPoint point)
    {
        try
        {
            var candidates = EnumerateTooltipWindows(point);
            if (candidates.Count == 0) return null;

            int queried = 0;
            foreach (var candidate in candidates)
            {
                if (queried++ >= TooltipMaxWindowsToQuery) break;
                string? text = ReadTooltipText(candidate);
                if (text is not null)
                {
                    if (_log.IsDebugEnabled) _log.Debug($"UIA: tooltip '{text}' ({candidate.ClassName}) a {candidate.Distance:F0} px");
                    return new UiTooltipInfo(text, candidate.Bounds);
                }
            }

            // nessun testo: si offre il rettangolo per l'OCR, ma solo se la finestra ha davvero l'aspetto di un suggerimento
            var nearest = candidates.FirstOrDefault(c => c.IsConfident);
            if (nearest is null) return null;
            if (_log.IsDebugEnabled) _log.Debug($"UIA: finestra di suggerimento senza testo ({nearest.ClassName}); si farà l'OCR di {nearest.Bounds}");
            return new UiTooltipInfo(null, nearest.Bounds);
        }
        catch (Exception ex)
        {
            LogFailure("FindTooltip", ex);
            return null;
        }
    }

    /// <param name="IsConfident">Suggerimento vero (vedi <see cref="IsConfidentTooltip"/>): solo allora si leggono i Text discendenti o si offre il rettangolo per l'OCR.</param>
    private sealed record TooltipWindow(IntPtr Handle, ScreenRect Bounds, string ClassName, double Distance, bool IsConfident);

    /// <summary>Classi che sono sempre suggerimenti: tooltips_class32 (Win32, Office) e Xaml_WindowedPopupClass (WinUI).</summary>
    internal static bool IsKnownTooltipClass(string className) =>
        className.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase)
        || className.Equals("Xaml_WindowedPopupClass", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finestre di WPF (Affinity): con la classe HwndWrapper WPF crea suggerimenti, ma anche menu, tendine e popup. La classe
    /// da sola la rende solo una candidata: si legge se espone un elemento ToolTip o se lo stile dice che è un suggerimento.
    /// </summary>
    internal static bool IsWpfPopupClass(string className) =>
        className.StartsWith("HwndWrapper", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Suggerimento vero: classe nota (<see cref="IsKnownTooltipClass"/>), oppure WS_EX_TRANSPARENT insieme a WS_EX_TOOLWINDOW
    /// e WS_EX_NOACTIVATE (i clic lo attraversano: i suggerimenti di Affinity hanno stile esteso 0x080800A8). Una finestra
    /// strumento che si può cliccare (menu, tendina, pannello fluttuante) non lo è.
    /// </summary>
    internal static bool IsConfidentTooltip(string className, long exStyle)
    {
        if (IsKnownTooltipClass(className)) return true;
        const long required = NativeMethods.WsExTransparent | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        return (exStyle & required) == required;
    }

    // Posizione di un vero suggerimento rispetto al puntatore (al 100%, scalati): stessi valori di
    // TextResolver.IsPlacedLikeTooltip nella logica, che questo progetto non riferisce; un test li confronta.
    internal const int TooltipMaxAbovePx = 10;
    internal const int TooltipMaxBelowPx = 100;
    internal const int TooltipMaxRightOfPointerPx = 60;
    internal const int TooltipMaxLeftOfPointerPx = 300;

    /// <summary>
    /// Vero se il rettangolo è dove si mette un suggerimento per il punto (bordo superiore fra 10 px sopra e 100 px sotto il
    /// puntatore, sinistro non oltre 60 px a destra, destro non oltre 300 px a sinistra, valori al 100% per la scala). Filtra
    /// le candidate prima di interrogarle: una tendina o un pannello vicino non nasconde più il suggerimento vero dietro.
    /// </summary>
    internal static bool IsPlacedLikeTooltip(ScreenRect bounds, ScreenPoint point, double dpi)
    {
        if (bounds.IsEmpty) return false;
        if (dpi <= 0 || double.IsNaN(dpi) || double.IsInfinity(dpi)) dpi = 1.0;
        bool vertical = bounds.Y >= point.Y - TooltipMaxAbovePx * dpi && bounds.Y <= point.Y + TooltipMaxBelowPx * dpi;
        bool horizontal = bounds.X <= point.X + TooltipMaxRightOfPointerPx * dpi && bounds.Right >= point.X - TooltipMaxLeftOfPointerPx * dpi;
        return vertical && horizontal;
    }

    /// <summary>
    /// Finestre di primo livello visibili, piccole, con aspetto da suggerimento e messe dove si mette un suggerimento per il
    /// punto (<see cref="IsPlacedLikeTooltip"/>), più vicine prima. Si saltano quelle di app bloccate: interrogarle con UIA
    /// terrebbe occupato il thread fino al tempo massimo.
    /// </summary>
    private static List<TooltipWindow> EnumerateTooltipWindows(ScreenPoint point)
    {
        var list = new List<TooltipWindow>();
        uint ownPid = (uint)Environment.ProcessId;
        double dpi = NativeMethods.GetDpiScaleAt(point.X, point.Y);

        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (!NativeMethods.GetWindowRect(hwnd, out var rc)) return true;
                var bounds = ElementSnapshot.ToScreenRect(rc.Left, rc.Top, rc.Right, rc.Bottom);
                if (bounds.IsEmpty || bounds.Height >= TooltipMaxHeightPx || bounds.Width >= TooltipMaxWidthPx) return true;

                double distance = bounds.DistanceTo(point);
                if (distance > TooltipMaxDistancePx) return true;
                if (!IsPlacedLikeTooltip(bounds, point, dpi)) return true;

                long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
                bool toolWindow = (exStyle & NativeMethods.WsExToolWindow) != 0;
                string className = NativeMethods.GetClassNameSafe(hwnd);
                if (!toolWindow && !IsKnownTooltipClass(className) && !IsWpfPopupClass(className)) return true;

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == ownPid) return true;
                if (NativeMethods.IsCloaked(hwnd)) return true;
                if (NativeMethods.IsHungAppWindow(hwnd)) return true;

                list.Add(new TooltipWindow(hwnd, bounds, className, distance, IsConfidentTooltip(className, exStyle)));
            }
            catch (Exception)
            {
                // finestra sparita durante l'enumerazione: si prosegue
            }
            return true;
        };
        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        list.Sort((a, b) =>
        {
            int byDistance = a.Distance.CompareTo(b.Distance);
            return byDistance != 0 ? byDistance : a.Bounds.Area.CompareTo(b.Bounds.Area);
        });
        return list;
    }

    /// <summary>
    /// Testo del suggerimento: elemento ToolTip (radice o discendente); altrimenti, solo per suggerimenti veri
    /// (<see cref="IsConfidentTooltip"/>: il nome di classe HwndWrapper non basta), i Text discendenti. null se la finestra
    /// è muta o non è un suggerimento (una tendina o un popup di WPF non si legge per intero).
    /// </summary>
    private string? ReadTooltipText(TooltipWindow window)
    {
        var root = ElementSnapshot.Safe(() => _s.Automation.ElementFromHandleBuildCache(window.Handle, _s.ElementCache), null);
        if (root is null) return null;
        var snap = ElementSnapshot.Read(root);
        if (snap.Kind == UiElementKind.ToolTip && snap.Name is not null) return snap.Name;

        var tip = ElementSnapshot.Safe(
            () => root.FindFirstBuildCache(UIA.TreeScope.TreeScope_Descendants, _s.ToolTipCondition, _s.ElementCache), null);
        if (tip is not null)
        {
            string? tipName = ElementSnapshot.Clean(ElementSnapshot.Safe(() => tip.CachedName, null));
            if (tipName is not null) return tipName;
        }

        // senza un elemento ToolTip, una finestra strumento o un popup di WPF potrebbe essere una barra fluttuante o una
        // tendina: si legge solo se lo stile dice che è un suggerimento
        if (tip is null && snap.Kind != UiElementKind.ToolTip && !window.IsConfident) return null;

        var texts = ElementSnapshot.Safe(
            () => root.FindAllBuildCache(UIA.TreeScope.TreeScope_Descendants, _s.TextCondition, _s.ElementCache), null);
        if (texts is not null)
        {
            int count = ElementSnapshot.Safe(() => texts.Length, 0);
            var parts = new List<string>();
            for (int i = 0; i < count && i < MaxDescendantTexts; i++)
            {
                var child = ElementSnapshot.Safe(() => texts.GetElement(i), null);
                if (child is null) continue;
                string? n = ElementSnapshot.Clean(ElementSnapshot.Safe(() => child.CachedName, null));
                if (n is not null && !parts.Contains(n, StringComparer.Ordinal)) parts.Add(n);
            }
            if (parts.Count > 0) return string.Join(Environment.NewLine, parts);
        }

        if (snap.Name is not null && snap.Kind is not (UiElementKind.Window or UiElementKind.Pane)) return snap.Name;
        return null;
    }

    // ------------------------------------------------------------------ errori

    private void LogFailure(string operation, Exception ex)
    {
        if (ex is COMException com && IsTransientHResult(com.HResult))
        {
            _log.Debug($"UIA: {operation} non disponibile (0x{com.HResult:X8})");
            return;
        }
        if (ex is COMException or InvalidCastException or NullReferenceException or ArgumentException
            or InvalidOperationException or UnauthorizedAccessException or TimeoutException
            or NotImplementedException or OverflowException or ExternalException)
        {
            _log.Warn($"UIA: {operation} fallita: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return;
        }
        _log.Error($"UIA: {operation} fallita con eccezione non prevista", ex);
    }

    /// <summary>Elemento sparito, app che non risponde, server RPC scollegato: normali nel percorso di lettura.</summary>
    private static bool IsTransientHResult(int hr) => hr switch
    {
        unchecked((int)0x80040201) => true, // UIA_E_ELEMENTNOTAVAILABLE
        unchecked((int)0x80131505) => true, // UIA_E_TIMEOUT
        unchecked((int)0x80040200) => true, // UIA_E_ELEMENTNOTENABLED
        unchecked((int)0x800706BA) => true, // RPC_S_SERVER_UNAVAILABLE
        unchecked((int)0x800706BE) => true, // RPC_S_CALL_FAILED
        unchecked((int)0x80010108) => true, // RPC_E_DISCONNECTED
        unchecked((int)0x80010012) => true, // RPC_E_SERVER_DIED_DNE
        unchecked((int)0x80004005) => true, // E_FAIL (provider che rifiuta il punto)
        _ => false,
    };
}
