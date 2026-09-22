// UiaPointReader.cs - reusable sample for "Punta e Ascolta".
// COM UIA3 (CUIAutomation8) through the NuGet package Interop.UIAutomationClient 10.19041.0.
// Verified on Windows 11 ARM64 + .NET 10 against Word / Excel 16.0.20326 (see probe-office.md).
//
// Threading: create and use this object on ONE dedicated MTA thread without windows.
// DPI: the process MUST be Per-Monitor-V2 DPI aware (manifest or SetProcessDpiAwarenessContext(-4)),
//      all coordinates are physical screen pixels.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UIA = Interop.UIAutomationClient;

namespace PuntaEAscolta.Uia;

public static class UiaIds
{
    // properties
    public const int BoundingRectangle = 30001, ProcessId = 30002, ControlType = 30003, LocalizedControlType = 30004,
        Name = 30005, AcceleratorKey = 30006, AccessKey = 30007, AutomationId = 30011, ClassName = 30012,
        HelpText = 30013, NativeWindowHandle = 30020, FrameworkId = 30024, ItemStatus = 30026,
        IsTextPatternAvailable = 30040, IsValuePatternAvailable = 30043, ValueValue = 30045,
        IsLegacyIAccessiblePatternAvailable = 30090, FullDescription = 30159;
    // patterns
    public const int ValuePattern = 10002, SelectionPattern = 10001, TextPattern = 10014, TogglePattern = 10015,
        LegacyIAccessiblePattern = 10018, TextPattern2 = 10024;
    // control types
    public const int Button = 50000, CheckBox = 50002, Edit = 50004, TabItem = 50019, Text = 50020,
        DataItem = 50029, Document = 50030, SplitButton = 50031, Pane = 50033, HeaderItem = 50035, StatusBar = 50017;
}

public enum SentenceStatus { Ok, NoElement, NoTextPattern, NotOnText, Error }

public sealed record SentenceResult(
    SentenceStatus Status,
    string? Sentence,          // cleaned sentence to speak (null unless Status == Ok)
    string? ParagraphRaw,      // raw paragraph text as returned by UIA (capped)
    int Offset,                // UTF-16 offset of the pointed position inside ParagraphRaw
    double DistancePx,         // distance of the point from the nearest rectangle of the pointed LINE (0 = inside)
    string? Error = null);

public sealed record SelectionResult(
    bool HasTextHost,          // an element with TextPattern was found
    bool HasSelection,         // selection is not just a caret
    bool PointerInside,        // the point is inside one of the selection rectangles
    string Text,               // selected text, raw
    double[] Rects);           // flat x,y,w,h,... physical pixels, one rectangle per visible line

public sealed record ElementDescription(
    int ControlType, string LocalizedControlType, string Name, string HelpText, string FullDescription,
    string Value, bool HasValuePattern, string LegacyName, string LegacyDescription, string LegacyHelp,
    string LegacyValue, string LegacyDefaultAction, string LegacyShortcut, int LegacyRole,
    string ClassName, string FrameworkId, string AutomationId, string AcceleratorKey, string AccessKey,
    string ItemStatus, int ProcessId, double Left, double Top, double Width, double Height)
{
    /// <summary>What the app should speak for a non-document element (label policy measured on Office).</summary>
    public string SpokenText
    {
        get
        {
            // Excel cell: Name is the coordinate ("C3"), the content is in ValuePattern.Value
            if (ControlType == UiaIds.DataItem && HasValuePattern) return Value;
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            if (!string.IsNullOrWhiteSpace(LegacyName)) return LegacyName;
            if (!string.IsNullOrWhiteSpace(HelpText)) return HelpText;
            if (!string.IsNullOrWhiteSpace(FullDescription)) return FullDescription;
            if (!string.IsNullOrWhiteSpace(LegacyDescription)) return LegacyDescription;
            return HasValuePattern ? Value : "";
        }
    }
}

public sealed class UiaPointReader
{
    public UIA.IUIAutomation Automation { get; }
    private readonly UIA.IUIAutomationTreeWalker _raw;

    /// <summary>Optional hook used by the probe to collect the milliseconds of every UIA call.</summary>
    public Action<string, double>? OnTiming;

    /// <summary>Cap for GetText on a paragraph / selection (UTF-16 units).</summary>
    public int MaxChars { get; set; } = 8000;

    /// <summary>Do not even try TextPattern when the document element is not one of these control types.</summary>
    public int MaxClimb { get; set; } = 8;

    public UiaPointReader(int connectionTimeoutMs = 1000, int transactionTimeoutMs = 3000)
    {
        var a = new UIA.CUIAutomation8();                 // CLSID that implements IUIAutomation2+ (timeouts)
        Automation = a;
        var a2 = (UIA.IUIAutomation2)a;
        a2.ConnectionTimeout = (uint)connectionTimeoutMs;
        a2.TransactionTimeout = (uint)transactionTimeoutMs;
        a2.AutoSetFocus = 0;                              // never move focus as a side effect
        _raw = a.RawViewWalker;
    }

    // ------------------------------------------------------------------ sentence under the pointer

    /// <summary>
    /// Sentence of a TextPattern document (Word, WordPad, browsers...) under the screen point.
    /// Algorithm: RangeFromPoint (degenerate range) -> validate against the rectangles of the enclosing LINE ->
    /// paragraph = clone expanded to Paragraph -> prefix = paragraph clone with End moved to caret.Start ->
    /// offset = prefix text length -> SentenceSplitter.SentenceAt(paragraphText, offset).
    /// </summary>
    /// <param name="toleranceLines">accepted distance from the pointed line, as a fraction of the LINE HEIGHT
    /// (vertical: toleranceLines * h, horizontal: 1.0 * h). Scales by itself with DPI and zoom.</param>
    public SentenceResult GetSentenceAtPoint(int x, int y, UIA.IUIAutomationElement? element = null, double toleranceLines = 0.6)
    {
        try
        {
            var pt = new UIA.tagPOINT { x = x, y = y };
            var el = element ?? T("ElementFromPoint", () => Automation.ElementFromPoint(pt));
            if (el is null) return new(SentenceStatus.NoElement, null, null, 0, double.NaN);

            var host = T("FindTextHost", () => FindTextHost(el));
            if (host is null) return new(SentenceStatus.NoTextPattern, null, null, 0, double.NaN);
            var tp = (UIA.IUIAutomationTextPattern)host.GetCurrentPattern(UiaIds.TextPattern);

            // degenerate range NEAREST to the point: also returned for margins / blank areas -> must validate
            var caret = T("RangeFromPoint", () => tp.RangeFromPoint(pt));
            if (caret is null) return new(SentenceStatus.NotOnText, null, null, 0, double.NaN);

            var line = caret.Clone();
            T("Expand(Line)", () => { line.ExpandToEnclosingUnit(UIA.TextUnit.TextUnit_Line); return 0; });
            var lineRects = T("Line.GetBoundingRectangles", () => ToDoubles(line.GetBoundingRectangles()));
            double dist = DistanceToRects(lineRects, x, y);
            if (!IsNearLine(lineRects, x, y, toleranceLines)) return new(SentenceStatus.NotOnText, null, null, 0, dist);

            var para = caret.Clone();
            T("Expand(Paragraph)", () => { para.ExpandToEnclosingUnit(UIA.TextUnit.TextUnit_Paragraph); return 0; });

            var prefix = para.Clone();
            T("MoveEndpointByRange", () =>
            {
                prefix.MoveEndpointByRange(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, caret,
                                           UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                return 0;
            });
            string before = T("prefix.GetText", () => prefix.GetText(MaxChars)) ?? "";
            string all = T("paragraph.GetText", () => para.GetText(MaxChars)) ?? "";

            if (before.Length >= MaxChars)
            {
                // huge paragraph (pointer more than MaxChars into it): read a window of +-6 LINES around the caret.
                // Moving by Line is much cheaper than moving by 600 Characters (measured 145 ms on Word).
                var win = caret.Clone();
                T("fallback.MoveByLine", () =>
                {
                    win.MoveEndpointByUnit(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, UIA.TextUnit.TextUnit_Line, -6);
                    win.MoveEndpointByUnit(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, UIA.TextUnit.TextUnit_Line, 6);
                    return 0;
                });
                var wprefix = win.Clone();
                wprefix.MoveEndpointByRange(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, caret,
                                            UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                before = wprefix.GetText(4000) ?? "";
                all = win.GetText(4000) ?? "";
            }

            int offset = Math.Min(before.Length, all.Length);
            string sentence = SentenceSplitter.SentenceAt(all, offset);
            if (sentence.Length == 0) return new(SentenceStatus.NotOnText, null, all, offset, dist);
            return new(SentenceStatus.Ok, sentence, all, offset, dist);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new(SentenceStatus.Error, null, null, 0, double.NaN, $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ selection

    /// <summary>
    /// Reads the selection of the text host under the point and tells whether the point is inside it.
    /// Returns false when there is no text host or nothing is selected (caret only).
    /// </summary>
    public bool TryGetSelectionAtPoint(int x, int y, out SelectionResult result, double tolerancePx = 2)
    {
        result = new(false, false, false, "", Array.Empty<double>());
        try
        {
            var pt = new UIA.tagPOINT { x = x, y = y };
            var el = T("ElementFromPoint", () => Automation.ElementFromPoint(pt));
            if (el is null) return false;
            var host = T("FindTextHost", () => FindTextHost(el));
            if (host is null) return false;
            result = ReadSelection(host, x, y, tolerancePx);
            return result.HasSelection;
        }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
    }

    /// <summary>Selection of the element that has the keyboard focus ("speak the current selection" hotkey).</summary>
    public bool TryGetFocusedSelection(out SelectionResult result)
    {
        result = new(false, false, false, "", Array.Empty<double>());
        try
        {
            var el = T("GetFocusedElement", () => Automation.GetFocusedElement());
            if (el is null) return false;
            var host = T("FindTextHost", () => FindTextHost(el));
            if (host is null) return false;
            result = ReadSelection(host, int.MinValue, int.MinValue, 0);
            return result.HasSelection;
        }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
    }

    private SelectionResult ReadSelection(UIA.IUIAutomationElement host, int x, int y, double tolerancePx)
    {
        var tp = (UIA.IUIAutomationTextPattern)host.GetCurrentPattern(UiaIds.TextPattern);
        var ranges = T("GetSelection", () => tp.GetSelection());
        if (ranges is null || ranges.Length == 0) return new(true, false, false, "", Array.Empty<double>());

        var sb = new StringBuilder();
        var rects = new List<double>();
        for (int i = 0; i < ranges.Length; i++)
        {
            var r = ranges.GetElement(i);
            sb.Append(T("selection.GetText", () => r.GetText(MaxChars)));
            rects.AddRange(T("selection.GetBoundingRectangles", () => ToDoubles(r.GetBoundingRectangles())));
        }
        string text = sb.ToString();
        bool has = text.Length > 0;                       // caret = one degenerate range = empty text
        var arr = rects.ToArray();
        bool inside = has && DistanceToRects(arr, x, y) <= tolerancePx;
        return new(true, has, inside, text, arr);
    }

    // ------------------------------------------------------------------ labels (ribbon, status bar, Excel cells...)

    public ElementDescription? DescribeElementAtPoint(int x, int y)
    {
        try
        {
            var el = T("ElementFromPoint", () => Automation.ElementFromPoint(new UIA.tagPOINT { x = x, y = y }));
            return el is null ? null : T("Describe", () => Describe(el));
        }
        catch (Exception ex) when (IsExpected(ex)) { return null; }
    }

    public ElementDescription Describe(UIA.IUIAutomationElement el)
    {
        string S(Func<string?> f) { try { return f() ?? ""; } catch (Exception ex) when (IsExpected(ex)) { return ""; } }
        string P(int id) => S(() => el.GetCurrentPropertyValue(id) as string);

        int ct = 0; try { ct = el.CurrentControlType; } catch (Exception ex) when (IsExpected(ex)) { }
        var rc = default(UIA.tagRECT); try { rc = el.CurrentBoundingRectangle; } catch (Exception ex) when (IsExpected(ex)) { }
        int pid = 0; try { pid = el.CurrentProcessId; } catch (Exception ex) when (IsExpected(ex)) { }

        string value = ""; bool hasValue = false;
        try
        {
            if (el.GetCurrentPattern(UiaIds.ValuePattern) is UIA.IUIAutomationValuePattern vp)
            { hasValue = true; value = vp.CurrentValue ?? ""; }
        }
        catch (Exception ex) when (IsExpected(ex)) { }

        string ln = "", ld = "", lh = "", lv = "", la = "", lk = ""; int role = 0;
        try
        {
            if (el.GetCurrentPattern(UiaIds.LegacyIAccessiblePattern) is UIA.IUIAutomationLegacyIAccessiblePattern lp)
            {
                ln = S(() => lp.CurrentName); ld = S(() => lp.CurrentDescription); lh = S(() => lp.CurrentHelp);
                lv = S(() => lp.CurrentValue); la = S(() => lp.CurrentDefaultAction); lk = S(() => lp.CurrentKeyboardShortcut);
                try { role = (int)lp.CurrentRole; } catch (Exception ex) when (IsExpected(ex)) { }
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { }

        return new ElementDescription(ct, S(() => el.CurrentLocalizedControlType), S(() => el.CurrentName),
            S(() => el.CurrentHelpText), P(UiaIds.FullDescription), value, hasValue, ln, ld, lh, lv, la, lk, role,
            S(() => el.CurrentClassName), S(() => el.CurrentFrameworkId), S(() => el.CurrentAutomationId),
            S(() => el.CurrentAcceleratorKey), S(() => el.CurrentAccessKey), S(() => el.CurrentItemStatus), pid,
            rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Nearest ancestor-or-self that supports TextPattern (raw view), or null.</summary>
    public UIA.IUIAutomationElement? FindTextHost(UIA.IUIAutomationElement el) => FindTextHost(el, out _);

    public UIA.IUIAutomationElement? FindTextHost(UIA.IUIAutomationElement el, out int levels)
    {
        levels = 0;
        UIA.IUIAutomationElement? cur = el;
        while (cur is not null && levels <= MaxClimb)
        {
            if (cur.GetCurrentPropertyValue(UiaIds.IsTextPatternAvailable) is bool b && b) return cur;
            cur = _raw.GetParentElement(cur);
            levels++;
        }
        return null;
    }

    public static double[] ToDoubles(object? safeArray) => safeArray switch
    {
        double[] d => d,
        Array a => a.Cast<object>().Select(Convert.ToDouble).ToArray(),
        _ => Array.Empty<double>()
    };

    /// <summary>rects = flat [x,y,w,h, x,y,w,h, ...]. 0 when the point is inside one rectangle; +inf when there is none.</summary>
    public static double DistanceToRects(double[] rects, int x, int y)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i + 3 < rects.Length; i += 4)
        {
            double dx = Math.Max(Math.Max(rects[i] - x, x - (rects[i] + rects[i + 2])), 0);
            double dy = Math.Max(Math.Max(rects[i + 1] - y, y - (rects[i + 1] + rects[i + 3])), 0);
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
        }
        return best;
    }

    /// <summary>True when the point is on (or close enough to) one of the line rectangles. Tolerance is relative
    /// to the rectangle height, so it follows font size, zoom and DPI.</summary>
    public static bool IsNearLine(double[] rects, int x, int y, double toleranceLines)
    {
        for (int i = 0; i + 3 < rects.Length; i += 4)
        {
            double h = rects[i + 3];
            if (h <= 0) continue;
            double dx = Math.Max(Math.Max(rects[i] - x, x - (rects[i] + rects[i + 2])), 0);
            double dy = Math.Max(Math.Max(rects[i + 1] - y, y - (rects[i + 1] + h)), 0);
            if (dx <= h && dy <= toleranceLines * h) return true;
        }
        return false;
    }

    public static bool IsExpected(Exception ex) => ex is COMException or TimeoutException or InvalidOperationException
        or UnauthorizedAccessException or ArgumentException or NotImplementedException or InvalidCastException
        or NullReferenceException;

    private TResult T<TResult>(string label, Func<TResult> f)
    {
        if (OnTiming is null) return f();
        long t0 = Stopwatch.GetTimestamp();
        try { return f(); }
        finally { OnTiming(label, Stopwatch.GetElapsedTime(t0).TotalMilliseconds); }
    }
}

/// <summary>Portable (no Windows dependency): belongs to the core project.</summary>
public static class SentenceSplitter
{
    // abbreviations that never close a sentence (compared without the final dot, case-insensitive)
    private static readonly HashSet<string> NeverEnd = new(StringComparer.OrdinalIgnoreCase)
    {
        "sig", "sigg", "sig.ra", "sig.na", "dott", "dott.ssa", "dr", "prof", "prof.ssa", "ing", "avv", "arch", "geom",
        "rag", "spett", "egr", "gent", "art", "artt", "pag", "pagg", "n", "nr", "tel", "cfr", "vs", "fig", "cap",
        "vol", "tab", "es", "p.es", "ca", "c.a", "mr", "mrs", "ms", "st", "e.g", "i.e"
    };

    private const string Terminators = ".!?\u2026";
    private const string Closers = "\"'\u00BB\u201D\u2019)]";
    private const string HardBreaks = "\r\n\v\f\a\u2028\u2029";

    /// <summary>Returns [start,end) spans; trailing white space belongs to the sentence it follows.</summary>
    public static List<(int Start, int End)> Split(string text)
    {
        var spans = new List<(int, int)>();
        int start = 0, i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (HardBreaks.IndexOf(c) >= 0)
            {
                int e = i + 1;
                while (e < n && (HardBreaks.IndexOf(text[e]) >= 0 || char.IsWhiteSpace(text[e]))) e++;
                // a break right after a closed sentence (paragraph mark after the question mark) belongs to that sentence
                if (i == start && spans.Count > 0) spans[^1] = (spans[^1].Item1, e);
                else spans.Add((start, e));
                start = i = e; continue;
            }
            if (Terminators.IndexOf(c) >= 0)
            {
                int e = i;
                while (e < n && Terminators.IndexOf(text[e]) >= 0) e++;          // "?!", "..."
                int runLen = e - i;
                while (e < n && Closers.IndexOf(text[e]) >= 0) e++;
                bool atEnd = e >= n;
                bool spaceAfter = !atEnd && char.IsWhiteSpace(text[e]);
                if (atEnd || spaceAfter)
                {
                    int next = e;
                    while (next < n && char.IsWhiteSpace(text[next]) && HardBreaks.IndexOf(text[next]) < 0) next++;
                    bool boundary = true;
                    bool dotLike = c == '.' || c == '\u2026';
                    if (dotLike && next < n && char.IsLower(text[next])) boundary = false;          // "ecc. ma", "Salva... poi"
                    if (boundary && c == '.' && runLen == 1)
                    {
                        int ts = i;
                        while (ts > start && !char.IsWhiteSpace(text[ts - 1]) && "([\u00AB\u201C\"'".IndexOf(text[ts - 1]) < 0) ts--;
                        string token = text.Substring(ts, i - ts);
                        if (NeverEnd.Contains(token)) boundary = false;                               // "sig. Rossi"
                        else if (token.Length == 1 && char.IsLetter(token[0]) && char.IsUpper(token[0])) boundary = false; // "G. Verdi"
                    }
                    if (boundary) { spans.Add((start, next)); start = next; }
                    i = Math.Max(next, i + 1); continue;
                }
                i = e; continue;
            }
            i++;
        }
        if (start < n) spans.Add((start, n));
        return spans;
    }

    /// <summary>Cleaned sentence that contains the UTF-16 offset (offset == text.Length -> last sentence).</summary>
    public static string SentenceAt(string text, int offset)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var spans = Split(text);
        if (spans.Count == 0) return "";
        offset = Math.Clamp(offset, 0, text.Length);
        var hit = spans[^1];
        foreach (var s in spans) if (offset >= s.Start && offset < s.End) { hit = s; break; }
        return Clean(text.Substring(hit.Start, hit.End - hit.Start));
    }

    /// <summary>Clean AFTER cutting, never before (offsets are computed on the raw text).</summary>
    public static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastSpace = true;
        foreach (char ch in s)
        {
            char c = ch;
            if (c == '\uFFFC' || c == '\a' || c == '\u200B' || c == '\uFEFF') continue;   // embedded object, table cell mark, ZW
            if (c == '\r' || c == '\n' || c == '\v' || c == '\f' || c == '\t' || c == '\u00A0') c = ' ';
            if (c == ' ') { if (!lastSpace) sb.Append(' '); lastSpace = true; }
            else { sb.Append(c); lastSpace = false; }
        }
        return sb.ToString().Trim();
    }
}
