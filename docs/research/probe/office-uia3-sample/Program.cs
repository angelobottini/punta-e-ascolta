// OfficeProbe - empirical probe of Word / Excel through COM UIA3. Throwaway driver; the reusable part is UiaPointReader.cs.
// SAFETY: creates its OWN Word/Excel instances, never touches any other instance (user's Word = PID 18000).
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using PuntaEAscolta.Uia;
using UIA = Interop.UIAutomationClient;

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("kernel32.dll")] public static extern bool GetProcessInformation(IntPtr h, int cls, out PMI info, int size);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] public struct PMI { public ushort Machine; public ushort Res; public uint Attr; }
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(128); GetClassName(h, sb, 128); return sb.ToString(); }
}

static class P
{
    const int UserWordPid = 18000;
    static StreamWriter _log = null!;
    static string _out = "";
    static readonly List<(string Label, double Ms)> Timings = new();
    static readonly List<string> Exceptions = new();
    static readonly List<(string Label, int X, int Y)> Marks = new();
    static string _ctx = "";
    static UiaPointReader R = null!;
    static UIA.IUIAutomation A => R.Automation;
    static int _myPid;

    static void W(string s = "") { Console.WriteLine(s); _log.WriteLine(s); _log.Flush(); }

    static T Time<T>(string label, Func<T> f)
    {
        long t0 = Stopwatch.GetTimestamp();
        try { return f(); }
        catch (Exception ex) { Exceptions.Add($"{_ctx}/{label}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"); throw; }
        finally { Timings.Add(($"{_ctx}/{label}", Stopwatch.GetElapsedTime(t0).TotalMilliseconds)); }
    }
    static double LastMs => Timings[^1].Ms;

    static void Retry(string label, Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (COMException ex) when (i < 15 && (ex.HResult == unchecked((int)0x80010001) || ex.HResult == unchecked((int)0x8001010A)))
            {
                Exceptions.Add($"COM {label}: 0x{ex.HResult:X8} {ex.Message} (retry {i + 1})");
                Thread.Sleep(300);
            }
        }
    }

    static string Esc(string? s, int max = 160)
    {
        if (s is null) return "<null>";
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (sb.Length > max) { sb.Append("[...]"); break; }
            sb.Append(c switch
            {
                '\r' => "\\r", '\n' => "\\n", '\v' => "\\v", '\a' => "\\a", '\t' => "\\t", '\f' => "\\f",
                _ when c < 0x20 || c == '￼' || c == ' ' || (c >= 0x2000 && c <= 0x200F) => $"\\u{(int)c:X4}",
                _ => c.ToString()
            });
        }
        return sb.ToString();
    }

    static List<int> Pids(string name) => Process.GetProcessesByName(name).Select(p => p.Id).OrderBy(i => i).ToList();

    static string Machine(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (Native.GetProcessInformation(p.Handle, 9, out var i, 8))
                return i.Machine switch { 0xAA64 => "ARM64", 0x8664 => "x64", 0x014c => "x86", _ => $"0x{i.Machine:X4}" };
        }
        catch { }
        return "?";
    }

    static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Console.OutputEncoding = Encoding.UTF8;
        string mode = args.Length > 0 ? args[0] : "word";
        _out = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(_out);
        _log = new StreamWriter(Path.Combine(_out, $"{mode}-log.txt"), false, new UTF8Encoding(false));

        W($"OfficeProbe mode={mode} {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"Process arch={RuntimeInformation.ProcessArchitecture} OS arch={RuntimeInformation.OSArchitecture} .NET={Environment.Version} apartment={Thread.CurrentThread.GetApartmentState()}");
        W($"Interop assembly: {typeof(UIA.CUIAutomation8).Assembly.FullName} @ {typeof(UIA.CUIAutomation8).Assembly.Location}");

        var wd = new Thread(() =>
        {
            Thread.Sleep(240_000);
            try { W("WATCHDOG: 240 s elapsed, giving up."); } catch { }
            try { if (_myPid != 0 && _myPid != UserWordPid) { Process.GetProcessById(_myPid).Kill(); } } catch { }
            Environment.Exit(3);
        }) { IsBackground = true };
        wd.Start();

        long t0 = Stopwatch.GetTimestamp();
        R = new UiaPointReader(1000, 3000);
        W($"new CUIAutomation8 + IUIAutomation2 timeouts: {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} ms");
        R.OnTiming = (l, ms) => Timings.Add(($"{_ctx}/reader.{l}", ms));

        int rc = 0;
        try { if (mode == "word") RunWord(); else if (mode == "excel") RunExcel(); else W("unknown mode"); }
        catch (Exception ex) { W("FATAL: " + ex); rc = 1; }

        W(); W("=== TIMINGS (ms) grouped by call ===");
        foreach (var g in Timings.GroupBy(t => t.Label[(t.Label.IndexOf('/') + 1)..]).OrderBy(g => g.Key))
        {
            var v = g.Select(t => t.Ms).OrderBy(d => d).ToList();
            W($"{g.Key,-44} n={v.Count,3} min={v[0],8:F1} med={v[v.Count / 2],8:F1} max={v[^1],8:F1}");
        }
        W(); W("=== CALLS > 300 ms ===");
        foreach (var t in Timings.Where(t => t.Ms > 300)) W($"{t.Label}: {t.Ms:F0} ms");
        W(); W("=== CALLS > 100 ms ===");
        foreach (var t in Timings.Where(t => t.Ms > 100 && t.Ms <= 300)) W($"{t.Label}: {t.Ms:F0} ms");
        W(); W("=== EXCEPTIONS ===");
        foreach (var e in Exceptions) W(e);
        _log.Dispose();
        return rc;
    }

    // =====================================================================================  WORD

    static readonly string[][] Paragraphs =
    {
        new[] { "Il sig. Rossi arriva alle 15.30. ", "Porta con sé 3,5 kg di mele! ", "Va bene?" },
        new[] { "Questo è un paragrafo volutamente lungo, scritto per occupare diverse righe della pagina e verificare che cosa succede quando una frase va a capo in modo automatico. ",
                "La seconda frase del paragrafo contiene parole comuni come tavolo, finestra e giardino, e continua ancora un poco per arrivare alla riga successiva senza fermarsi. ",
                "Matteo disegna ogni giorno con il computer: usa il mouse, sceglie i colori e poi salva il lavoro nella cartella dei documenti. ",
                "Alla fine della giornata spegne tutto e va a cena con la famiglia." },
        new[] { "Oggi Matteo è contento \U0001F600 perché ha finito il disegno. ", "La dott.ssa Bianchi lo ha visto ieri e ha detto: «Bravo!». ", "Costa 12.50 euro, ecc. ma non importa." },
        new[] { "Prima riga con interruzione manuale\v", "Seconda riga dopo l'interruzione. ", "Apri il file relazione.docx e premi Salva... ", "Poi chiudi tutto. ", "Hai capito?" },
    };

    // (paragraph, sentence, word, where: 0 = centre, -1 = left edge + 1px, +1 = right edge - 1px)
    static readonly (int P, int S, string Word, int Where)[] Targets =
    {
        (0, 0, "Rossi", 0), (0, 1, "mele", 0), (0, 2, "bene", 0),
        (1, 1, "giardino", 0), (1, 1, "fermarsi", 0), (1, 2, "cartella", 0),
        (2, 0, "disegno", 0), (2, 1, "Bianchi", 0), (2, 2, "importa", 0),
        (3, 1, "Seconda", 0), (3, 3, "chiudi", 0),
        (0, 0, "15.30.", +1), (0, 1, "Porta", -1), (0, 2, "bene?", +1), (1, 0, "automatico.", +1), (1, 1, "La", -1), (3, 4, "capito?", +1),
    };

    static void RunWord()
    {
        _ctx = "word";
        var before = Pids("WINWORD");
        W($"WINWORD PIDs before: [{string.Join(",", before)}]");
        if (!before.Contains(UserWordPid)) W("NOTE: user's Word PID 18000 is not running at start.");

        dynamic? app = null, doc = null;
        bool mine = false;
        try
        {
            var type = Type.GetTypeFromProgID("Word.Application") ?? throw new InvalidOperationException("Word.Application not registered");
            app = Time("COM CreateInstance(Word.Application)", () => Activator.CreateInstance(type)!);
            W($"CreateInstance: {LastMs:F0} ms");
            Thread.Sleep(500);
            var after = Pids("WINWORD");
            var created = after.Except(before).ToList();
            W($"WINWORD PIDs after: [{string.Join(",", after)}] created: [{string.Join(",", created)}]");
            if (created.Count != 1 || created[0] == UserWordPid)
            {
                W("ABORT: could not identify exactly one NEW Word process. Releasing the COM reference without Quit.");
                return;
            }
            _myPid = created[0];
            File.WriteAllText(Path.Combine(_out, "my-word-pid.txt"), _myPid.ToString());
            W($"My Word PID = {_myPid} machine={Machine(_myPid)} (user's PID {UserWordPid} machine={Machine(UserWordPid)})");

            Retry("DisplayAlerts", () => app.DisplayAlerts = 0);
            W($"Word Version={app.Version} Build={app.Build}");
            Retry("Documents.Add", () => doc = app.Documents.Add());
            Retry("Visible", () => app.Visible = true);
            dynamic win = app.ActiveWindow;
            IntPtr hwnd = new IntPtr((int)win.Hwnd);
            Native.GetWindowThreadProcessId(hwnd, out uint hp);
            W($"ActiveWindow.Hwnd=0x{hwnd:X} class={Native.ClassOf(hwnd)} pid={hp}");
            if (hp != _myPid || hp == UserWordPid) { W("ABORT: window PID mismatch."); return; }
            mine = true;

            Retry("WindowState", () => app.WindowState = 0);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 60, 20, 1800, 1100, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Retry("View", () => { win.View.Type = 3; win.View.Zoom.Percentage = 100; });
            uint dpi = Native.GetDpiForWindow(hwnd);
            W($"Window DPI={dpi} (scale {dpi / 96.0:P0}); view=print zoom=100%");

            string full = string.Join("\r", Paragraphs.Select(p => string.Concat(p)));
            Retry("Content.Text", () => doc.Content.Text = full);
            Retry("Repaginate", () => doc.Repaginate());
            try { app.Activate(); } catch (Exception ex) { Exceptions.Add("COM app.Activate: " + ex.Message); }
            Thread.Sleep(2500);
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint fg);
            W($"Foreground window PID after app.Activate = {fg} (mine={_myPid})");

            string docText = (string)doc.Content.Text;
            W($"doc.Content.Text length={docText.Length} (.NET string length of inserted text={full.Length})");
            W($"COM text escaped: {Esc(docText, 2000)}");

            // ---------- 1. sentence under pointer
            W(); W("########## 1. SENTENCE UNDER THE POINTER ##########");
            int ok = 0, n = 0;
            int textLeft = int.MaxValue, textRight = 0;
            var paraBottom = new Dictionary<int, int>(); var paraTop = new Dictionary<int, int>();
            foreach (var t in Targets)
            {
                string sentence = Paragraphs[t.P][t.S];
                int sIdx = docText.IndexOf(sentence, StringComparison.Ordinal);
                int wIdx = sentence.IndexOf(t.Word, StringComparison.Ordinal);
                if (sIdx < 0 || wIdx < 0) { W($"target {t.Word}: not found in document text"); continue; }
                int start = sIdx + wIdx, end = start + t.Word.Length;
                dynamic rng = doc.Range(start, end);
                string rtext = (string)rng.Text;
                int l = 0, tp = 0, w = 0, h = 0;
                try { Time("COM Window.GetPoint", () => { win.GetPoint(ref l, ref tp, ref w, ref h, rng); return 0; }); }
                catch (Exception ex) { W($"target {t.Word}: GetPoint failed: {ex.Message}"); continue; }
                int x = t.Where == 0 ? l + w / 2 : t.Where < 0 ? l + 1 : l + w - 2;
                int y = tp + h / 2;
                textLeft = Math.Min(textLeft, l); textRight = Math.Max(textRight, l + w);
                paraTop[t.P] = Math.Min(paraTop.GetValueOrDefault(t.P, int.MaxValue), tp);
                paraBottom[t.P] = Math.Max(paraBottom.GetValueOrDefault(t.P, 0), tp + h);
                string label = $"T{++n} P{t.P + 1}S{t.S + 1} '{t.Word}' {(t.Where == 0 ? "centre" : t.Where < 0 ? "LEFT EDGE" : "RIGHT EDGE")}";
                W(); W($"--- {label}: COM range [{start},{end}) text='{Esc(rtext)}' GetPoint l={l} t={tp} w={w} h={h} ({LastMs:F1} ms)");
                Marks.Add(($"T{n}", x, y));
                var res = ProbeTextPoint(label, x, y, hwnd, detailed: true);
                if (n == 1) Ancestors(x, y);
                string expected = SentenceSplitter.Clean(sentence);
                bool good = res.Status == SentenceStatus.Ok && res.Sentence == expected;
                if (good) ok++;
                W($"    EXPECTED: {expected}");
                W($"    RESULT  : {(good ? "CORRECT" : "WRONG")}");
            }
            W(); W($"SENTENCE TARGETS CORRECT: {ok}/{n}");

            // ---------- document-level facts
            W(); W("########## document element ##########");
            _ctx = "word-doc";
            {
                var el = A.ElementFromPoint(new UIA.tagPOINT { x = Marks[0].X, y = Marks[0].Y });
                var host = R.FindTextHost(el)!;
                var d = Time("Describe(document)", () => R.Describe(host));
                W($"doc element: ct={d.ControlType} class={d.ClassName} fw={d.FrameworkId} aid={d.AutomationId} Name.len={d.Name.Length} Name='{Esc(d.Name, 80)}' Value.len={d.Value.Length} hasValue={d.HasValuePattern} LegacyName.len={d.LegacyName.Length} LegacyValue.len={d.LegacyValue.Length} LegacyDesc.len={d.LegacyDescription.Length} rect=({d.Left},{d.Top},{d.Width},{d.Height})");
                var tpat = (UIA.IUIAutomationTextPattern)host.GetCurrentPattern(UiaIds.TextPattern);
                W($"SupportedTextSelection={tpat.SupportedTextSelection}; TextPattern2={(host.GetCurrentPattern(UiaIds.TextPattern2) is null ? "no" : "yes")}");
                var dr = tpat.DocumentRange;
                string all = Time("DocumentRange.GetText(-1)", () => dr.GetText(-1));
                W($"DocumentRange.GetText(-1): len={all.Length} in {LastMs:F1} ms; escaped: {Esc(all, 2000)}");
                var vis = Time("GetVisibleRanges", () => tpat.GetVisibleRanges());
                W($"GetVisibleRanges: {vis.Length} range(s) in {LastMs:F1} ms");
                var kids = Time("FindAll(children of document)", () => host.FindAll(UIA.TreeScope.TreeScope_Children, A.CreateTrueCondition()));
                W($"document children: {kids.Length} ({LastMs:F1} ms)");
                for (int i = 0; i < Math.Min(kids.Length, 8); i++)
                {
                    var k = kids.GetElement(i);
                    W($"   child[{i}] ct={k.CurrentControlType} class={k.CurrentClassName} Name.len={(k.CurrentName ?? "").Length} Name='{Esc(k.CurrentName, 60)}'");
                }
            }

            // ---------- 2. selection
            W(); W("########## 2. SELECTION ##########");
            _ctx = "word-sel";
            {
                string s1 = Paragraphs[1][1];
                int a = docText.IndexOf(s1, StringComparison.Ordinal) + s1.IndexOf("tavolo", StringComparison.Ordinal);
                string s2 = Paragraphs[1][2];
                int b = docText.IndexOf(s2, StringComparison.Ordinal) + s2.IndexOf("colori", StringComparison.Ordinal) + "colori".Length;
                bool sfw = Native.SetForegroundWindow(hwnd);
                Thread.Sleep(700);
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint fg2);
                W($"SetForegroundWindow(my Word) returned {sfw}; foreground PID now={fg2} (mine={_myPid})");
                dynamic sel = doc.Range(a, b);
                string comText = (string)sel.Text;
                Retry("Range.Select", () => sel.Select());
                Thread.Sleep(600);
                W($"COM selected [{a},{b}) len={comText.Length}: '{Esc(comText, 400)}'");

                (int x, int y) PointOf(string sentence, string word)
                {
                    int s = docText.IndexOf(sentence, StringComparison.Ordinal) + sentence.IndexOf(word, StringComparison.Ordinal);
                    dynamic r = doc.Range(s, s + word.Length);
                    int l = 0, tp = 0, w = 0, h = 0; win.GetPoint(ref l, ref tp, ref w, ref h, r);
                    return (l + w / 2, tp + h / 2);
                }
                var probes = new (string Label, (int x, int y) Pt, bool Expected)[]
                {
                    ("inside: 'finestra' (first selected line)", PointOf(s1, "finestra"), true),
                    ("inside: 'fermarsi' (middle)", PointOf(s1, "fermarsi"), true),
                    ("inside: 'mouse' (last part)", PointOf(s2, "mouse"), true),
                    ("outside: 'comuni' (same line, before selection)", PointOf(s1, "comuni"), false),
                    ("outside: 'salva' (just after selection)", PointOf(s2, "salva"), false),
                    ("outside: 'Rossi' (other paragraph)", PointOf(Paragraphs[0][0], "Rossi"), false),
                    ("outside: 'famiglia' (end of paragraph)", PointOf(Paragraphs[1][3], "famiglia"), false),
                };
                int good = 0;
                foreach (var p in probes)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    bool has = R.TryGetSelectionAtPoint(p.Pt.x, p.Pt.y, out var sr);
                    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    Timings.Add(("word-sel/TryGetSelectionAtPoint TOTAL", ms));
                    bool correct = sr.PointerInside == p.Expected;
                    if (correct) good++;
                    W($"{p.Label} @({p.Pt.x},{p.Pt.y}): hasSel={has} inside={sr.PointerInside} expected={p.Expected} -> {(correct ? "CORRECT" : "WRONG")} ({ms:F1} ms) textLen={sr.Text.Length} rects={sr.Rects.Length / 4}");
                    Marks.Add((p.Expected ? "Sin" : "Sout", p.Pt.x, p.Pt.y));
                }
                W($"SELECTION CLASSIFICATION CORRECT: {good}/{probes.Length}");
                R.TryGetSelectionAtPoint(probes[0].Pt.x, probes[0].Pt.y, out var full1);
                W($"UIA selection text == COM text: {full1.Text == comText}; UIA='{Esc(full1.Text, 400)}'");
                for (int i = 0; i + 3 < full1.Rects.Length; i += 4) W($"   rect[{i / 4}] x={full1.Rects[i]:F0} y={full1.Rects[i + 1]:F0} w={full1.Rects[i + 2]:F0} h={full1.Rects[i + 3]:F0}");

                long tf = Stopwatch.GetTimestamp();
                bool fh = R.TryGetFocusedSelection(out var fsel);
                W($"TryGetFocusedSelection: has={fh} hostFound={fsel.HasTextHost} textLen={fsel.Text.Length} ({Stopwatch.GetElapsedTime(tf).TotalMilliseconds:F1} ms)");
                try
                {
                    var fe = A.GetFocusedElement();
                    W($"GetFocusedElement: pid={fe.CurrentProcessId} ct={fe.CurrentControlType} class={Esc(fe.CurrentClassName, 40)} aid={fe.CurrentAutomationId} Name.len={(fe.CurrentName ?? "").Length} textPattern={fe.GetCurrentPropertyValue(UiaIds.IsTextPatternAvailable)}");
                }
                catch (Exception ex) { W("GetFocusedElement failed: " + ex.Message); }

                // collapse selection -> caret only
                Retry("Collapse", () => { dynamic c = doc.Range(0, 0); c.Select(); });
                Thread.Sleep(300);
                bool has0 = R.TryGetSelectionAtPoint(probes[0].Pt.x, probes[0].Pt.y, out var none);
                W($"after collapsing to a caret: hasSel={has0} host={none.HasTextHost} textLen={none.Text.Length} rects={none.Rects.Length / 4}");
            }

            // ---------- 4. empty areas (before the ribbon so the document is untouched)
            W(); W("########## 4. EMPTY AREAS ##########");
            _ctx = "word-empty";
            {
                var el0 = A.ElementFromPoint(new UIA.tagPOINT { x = Marks[0].X, y = Marks[0].Y });
                var host = R.FindTextHost(el0)!;
                var dr = host.CurrentBoundingRectangle;
                W($"document element rect: l={dr.left} t={dr.top} r={dr.right} b={dr.bottom}; text columns approx x=[{textLeft},{textRight}]");
                int lastBottom = paraBottom.Values.Max();
                int p1y = (paraTop[0] + paraBottom[0]) / 2;
                var pts = new (string Label, int X, int Y)[]
                {
                    ("E1 blank page area 200 px below last paragraph", textLeft + 300, lastBottom + 200),
                    ("E2 blank to the RIGHT of the short line of P1 (inside text column)", textRight - 40, p1y),
                    ("E3 left page MARGIN beside P2 (60 px left of text)", textLeft - 60, (paraTop[1] + paraBottom[1]) / 2),
                    ("E4 top page margin above P1", textLeft + 300, paraTop[0] - 70),
                    ("E5 area OUTSIDE the page (left of it)", dr.left - 150, (paraTop[1] + paraBottom[1]) / 2),
                    ("E6 area OUTSIDE the page (right of it)", dr.right + 150, (paraTop[1] + paraBottom[1]) / 2),
                    ("E7 gap between P1 and P2 (7 px below the P1 line)", textLeft + 100, paraTop[0] + 1 + 22 + 7),
                    ("E8 page right margin beside P2", dr.right - 40, (paraTop[1] + paraBottom[1]) / 2),
                };
                foreach (var p in pts)
                {
                    W(); W($"--- {p.Label} @({p.X},{p.Y}) pixel={PixelAt(p.X, p.Y)}");
                    Marks.Add((p.Label[..2], p.X, p.Y));
                    ProbeTextPoint(p.Label, p.X, p.Y, hwnd, detailed: true);
                    if (p.Label.StartsWith("E5") || p.Label.StartsWith("E4")) Ancestors(p.X, p.Y);
                }
            }

            // ---------- 3. ribbon / tab / status bar
            W(); W("########## 3. RIBBON, TAB HEADER, STATUS BAR ##########");
            _ctx = "word-ribbon";
            RibbonProbe(hwnd, new[] { "Grassetto", "Corsivo", "Centra", "Allinea al centro", "Elenchi puntati", "Copia formato", "Incolla", "Colore carattere" },
                        new[] { "Inserisci", "Layout" }, "word");

            SaveScreenshot(hwnd, "word-window.png");
            ExtraWordPhases(app, doc, win, hwnd);
        }
        finally
        {
            _ctx = "word-cleanup";
            if (app is not null)
            {
                if (mine)
                {
                    try { if (doc is not null) { doc.Saved = true; doc.Close(0); } } catch (Exception ex) { W("doc.Close: " + ex.Message); }
                    try { app.NormalTemplate.Saved = true; } catch { }
                    try { app.Quit(0); } catch (Exception ex) { W("app.Quit: " + ex.Message); }
                }
                try { if (doc is not null) Marshal.FinalReleaseComObject(doc); } catch { }
                try { Marshal.FinalReleaseComObject(app); } catch { }
                doc = null; app = null;
                GC.Collect(); GC.WaitForPendingFinalizers();
            }
            VerifyGone("WINWORD");
        }
    }

    static (int x, int y) RangePoint(dynamic win, dynamic rng, int where = 0)
    {
        int l = 0, t = 0, w = 0, h = 0; win.GetPoint(ref l, ref t, ref w, ref h, rng);
        return (where == 0 ? l + w / 2 : where < 0 ? l + 1 : l + w - 2, t + Math.Min(h, 24) / 2 + 1);
    }

    static void ExtraWordPhases(dynamic app, dynamic doc, dynamic win, IntPtr hwnd)
    {
        // ---------- zoom 150 %: are coordinates still consistent?
        W(); W("########## 1b. SAME TEXT AT ZOOM 150 % ##########");
        _ctx = "word-zoom";
        Retry("zoom", () => win.View.Zoom.Percentage = 150);
        Thread.Sleep(1200);
        string docText = (string)doc.Content.Text;
        foreach (var t in new[] { (0, 1, "mele"), (1, 1, "giardino"), (2, 0, "disegno") })
        {
            string sentence = Paragraphs[t.Item1][t.Item2];
            int s = docText.IndexOf(sentence, StringComparison.Ordinal) + sentence.IndexOf(t.Item3, StringComparison.Ordinal);
            try
            {
                (int x, int y) p = RangePoint(win, doc.Range(s, s + t.Item3.Length));
                SentenceResult r = R.GetSentenceAtPoint(p.x, p.y);
                W($"zoom150 '{t.Item3}' @({p.x},{p.y}): status={r.Status} dist={r.DistancePx:F1} -> {(r.Sentence == SentenceSplitter.Clean(sentence) ? "CORRECT" : "WRONG: " + r.Sentence)}");
            }
            catch (Exception ex) { W($"zoom150 '{t.Item3}': {ex.GetType().Name} {ex.Message}"); }
        }
        Retry("zoom", () => win.View.Zoom.Percentage = 100);
        Thread.Sleep(800);

        // ---------- table + inline picture
        W(); W("########## 1c. TABLE CELLS AND INLINE PICTURE ##########");
        _ctx = "word-table";
        Marks.Clear();
        string img = Path.Combine(_out, "probe-image.png");
        using (var bmp = new Bitmap(420, 130))
        {
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.DarkGreen);
            using var f = new Font("Arial", 22, FontStyle.Bold);
            g.DrawString("TESTO NELLA FOTO", f, Brushes.White, 20, 40);
            bmp.Save(img, ImageFormat.Png);
        }
        dynamic? tbl = null, pic = null;
        Retry("table", () =>
        {
            doc.Content.InsertParagraphAfter();
            int end = (int)doc.Content.End - 1;
            tbl = doc.Tables.Add(doc.Range(end, end), 2, 2);
            tbl.Borders.Enable = 1;
            tbl.Cell(1, 1).Range.Text = "Nome del prodotto. Mele rosse del Trentino.";
            tbl.Cell(1, 2).Range.Text = "Prezzo";
            tbl.Cell(2, 1).Range.Text = "Pere. Dolci e mature!";
            tbl.Cell(2, 2).Range.Text = "3,50 euro";
        });
        Retry("picture", () =>
        {
            doc.Content.InsertParagraphAfter();
            int end = (int)doc.Content.End - 1;
            pic = doc.InlineShapes.AddPicture(img, false, true, doc.Range(end, end));
        });
        Thread.Sleep(1500);
        W($"COM text of cell(1,1): '{Esc((string)tbl!.Cell(1, 1).Range.Text)}'");
        foreach (var t in new[] { (1, 1, "Trentino", "Mele rosse del Trentino."), (1, 2, "Prezzo", "Prezzo"), (2, 1, "Pere", "Pere."), (2, 2, "euro", "3,50 euro") })
        {
            dynamic cr = tbl.Cell(t.Item1, t.Item2).Range;
            string ct = (string)cr.Text; int idx = ct.IndexOf(t.Item3, StringComparison.Ordinal);
            int st = (int)cr.Start + idx;
            (int x, int y) p = RangePoint(win, doc.Range(st, st + t.Item3.Length));
            W(); W($"--- table cell({t.Item1},{t.Item2}) '{t.Item3}' @({p.x},{p.y})");
            Marks.Add(($"c{t.Item1}{t.Item2}", p.x, p.y));
            SentenceResult r = ProbeTextPoint("cell", p.x, p.y, hwnd, detailed: true);
            W($"    EXPECTED: {t.Item4} -> {(r.Sentence == t.Item4 ? "CORRECT" : "WRONG")}");
            if (t.Item1 == 1 && t.Item2 == 1) Ancestors(p.x, p.y);
        }
        try
        {
            int l = 0, tp = 0, w = 0, h = 0; win.GetPoint(ref l, ref tp, ref w, ref h, pic!.Range);
            int x = l + w / 2, y = tp + h / 2;
            W(); W($"--- inline picture: GetPoint l={l} t={tp} w={w} h={h} -> ({x},{y}) pixel={PixelAt(x, y)}");
            Marks.Add(("img", x, y));
            ProbeTextPoint("picture", x, y, hwnd, detailed: true);
            Ancestors(x, y);
            PrintDescription("DescribeElementAtPoint(picture)", R.DescribeElementAtPoint(x, y));
        }
        catch (Exception ex) { W($"picture probe: {ex.GetType().Name} {ex.Message}"); }
        SaveScreenshot(hwnd, "word-window-table.png");

        // ---------- big document: 1 normal paragraph + 1 giant paragraph (21 k chars) + 400 normal paragraphs
        W(); W("########## 6b. BIG DOCUMENT ##########");
        _ctx = "word-big";
        Marks.Clear();
        string p2 = string.Concat(Paragraphs[1]);
        string giant = string.Join(" ", Enumerable.Repeat(p2, 40));
        string big = p2 + "\r" + giant + "\r" + string.Join("\r", Enumerable.Repeat(p2, 400));
        long t0 = Stopwatch.GetTimestamp();
        Retry("big text", () => doc.Content.Text = big);
        Retry("Repaginate", () => doc.Repaginate());
        Thread.Sleep(3000);
        W($"big document: {big.Length} chars inserted in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms (incl. 3 s sleep); pages={doc.ComputeStatistics(2)}");
        string s2 = Paragraphs[1][1];
        int a1 = p2.IndexOf("giardino", StringComparison.Ordinal);
        int a2 = p2.Length + 1 + 2 * (p2.Length + 1) + p2.IndexOf("giardino", StringComparison.Ordinal);
        foreach (var (label, start, maxChars) in new[] { ("normal paragraph, MaxChars=8000", a1, 8000), ("GIANT paragraph 3rd repetition, MaxChars=8000", a2, 8000),
                                                         ("GIANT paragraph 3rd repetition, MaxChars=1000 (window fallback)", a2, 1000), ("GIANT paragraph, MaxChars=40000", a2, 40000) })
        {
            try
            {
                dynamic rng = doc.Range(start, start + 8);
                (int x, int y) p = RangePoint(win, rng);
                R.MaxChars = maxChars;
                long t1 = Stopwatch.GetTimestamp();
                SentenceResult r = R.GetSentenceAtPoint(p.x, p.y);
                double ms = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                Timings.Add(("word-big/GetSentenceAtPoint TOTAL", ms));
                W($"{label}: COM text='{(string)rng.Text}' @({p.x},{p.y}) status={r.Status} offset={r.Offset} paraLen={r.ParagraphRaw?.Length} total={ms:F1} ms -> {(r.Sentence == SentenceSplitter.Clean(s2) ? "CORRECT" : "WRONG: " + Esc(r.Sentence, 200))}");
                Marks.Add(("big", p.x, p.y));
            }
            catch (Exception ex) { W($"{label}: {ex.GetType().Name} {ex.Message}"); }
        }
        R.MaxChars = 8000;
        try
        {
            var el = A.ElementFromPoint(new UIA.tagPOINT { x = Marks[0].X, y = Marks[0].Y });
            var host = R.FindTextHost(el)!;
            var tpat = (UIA.IUIAutomationTextPattern)host.GetCurrentPattern(UiaIds.TextPattern);
            W($"host under pointer in big doc: ct={host.CurrentControlType} Name='{Esc(host.CurrentName, 60)}'");
            string all = Time("DocumentRange.GetText(-1) big", () => tpat.DocumentRange.GetText(-1));
            W($"DocumentRange.GetText(-1): {all.Length} chars in {LastMs:F1} ms");
            var pr = tpat.RangeFromPoint(new UIA.tagPOINT { x = Marks[1].X, y = Marks[1].Y });
            pr.ExpandToEnclosingUnit(UIA.TextUnit.TextUnit_Paragraph);
            string gp = Time("giant paragraph GetText(-1)", () => pr.GetText(-1)); W($"giant paragraph GetText(-1): {gp.Length} chars in {LastMs:F1} ms");
            Time("giant paragraph GetBoundingRectangles", () => UiaPointReader.ToDoubles(pr.GetBoundingRectangles())); W($"giant paragraph GetBoundingRectangles: {LastMs:F1} ms");
            UIA.IUIAutomationElement? docEl = host;
            var rw = A.RawViewWalker;
            for (int i = 0; i < 4 && docEl is not null && docEl.CurrentControlType != UiaIds.Document; i++) docEl = rw.GetParentElement(docEl);
            if (docEl is not null && docEl.CurrentControlType == UiaIds.Document)
            {
                var dtp = (UIA.IUIAutomationTextPattern)docEl.GetCurrentPattern(UiaIds.TextPattern);
                string whole = Time("Document(_WwG).DocumentRange.GetText(-1) big", () => dtp.DocumentRange.GetText(-1));
                W($"Document element (_WwG) DocumentRange.GetText(-1): {whole.Length} chars in {LastMs:F1} ms; Name.len={(docEl.CurrentName ?? "").Length}");
                string capped = Time("Document(_WwG).DocumentRange.GetText(2000) big", () => dtp.DocumentRange.GetText(2000));
                W($"Document element (_WwG) DocumentRange.GetText(2000): {capped.Length} chars in {LastMs:F1} ms");
            }
            var pg = tpat.RangeFromPoint(new UIA.tagPOINT { x = Marks[1].X, y = Marks[1].Y });
            Time("Expand(Page)", () => { pg.ExpandToEnclosingUnit(UIA.TextUnit.TextUnit_Page); return 0; });
            W($"Expand(Page): {LastMs:F1} ms, text len={pg.GetText(-1).Length}");
        }
        catch (Exception ex) { W($"big doc extras: {ex.GetType().Name} {ex.Message}"); }
        SaveScreenshot(hwnd, "word-window-big.png");
    }

    static void VerifyGone(string procName)
    {
        if (_myPid == 0) return;
        bool gone = false;
        for (int i = 0; i < 40 && !gone; i++) { Thread.Sleep(250); gone = !Pids(procName).Contains(_myPid); }
        W($"cleanup: my {procName} PID {_myPid} gone={gone}; user's WINWORD {UserWordPid} alive={Pids("WINWORD").Contains(UserWordPid)}");
    }

    static string PixelAt(int x, int y)
    {
        using var bmp = new Bitmap(1, 1);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
        var c = bmp.GetPixel(0, 0);
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    static void SaveScreenshot(IntPtr hwnd, string file)
    {
        Native.GetWindowRect(hwnd, out var r);
        int w = r.r - r.l, h = r.b - r.t;
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.l, r.t, 0, 0, new Size(w, h));
            using var pen = new Pen(Color.Red, 2); using var font = new Font("Segoe UI", 9); using var br = new SolidBrush(Color.Blue);
            foreach (var m in Marks)
            {
                int x = m.X - r.l, y = m.Y - r.t;
                g.DrawLine(pen, x - 6, y, x + 6, y); g.DrawLine(pen, x, y - 6, x, y + 6);
                g.DrawString(m.Label, font, br, x + 4, y + 4);
            }
        }
        bmp.Save(Path.Combine(_out, file), ImageFormat.Png);
        W($"screenshot saved: {file} (window rect {r.l},{r.t},{w}x{h})");
    }

    /// <summary>Detailed dump of what UIA returns at a point of a text document + final answer of the reusable method.</summary>
    static SentenceResult ProbeTextPoint(string label, int x, int y, IntPtr myRoot, bool detailed)
    {
        string saved = _ctx;
        try
        {
            var np = new Native.POINT { x = x, y = y };
            IntPtr hw = Native.WindowFromPoint(np); IntPtr root = Native.GetAncestor(hw, 2);
            Native.GetWindowThreadProcessId(hw, out uint pid);
            W($"    WindowFromPoint: hwnd=0x{hw:X} class={Native.ClassOf(hw)} pid={pid} rootIsMine={(root == myRoot)}");
            if (pid != _myPid) { W("    !! point is not over my window, skipping"); return new(SentenceStatus.NoElement, null, null, 0, double.NaN); }

            var pt = new UIA.tagPOINT { x = x, y = y };
            var el = Time("ElementFromPoint", () => A.ElementFromPoint(pt));
            W($"    ElementFromPoint: ct={el.CurrentControlType} ({el.CurrentLocalizedControlType}) class='{el.CurrentClassName}' fw={el.CurrentFrameworkId} Name.len={(el.CurrentName ?? "").Length} Name='{Esc(el.CurrentName, 70)}' ({LastMs:F1} ms)");
            int levels = 0;
            var host = Time("FindTextHost", () => R.FindTextHost(el, out levels));
            W($"    TextPattern host: {(host is null ? "NONE" : $"ct={host.CurrentControlType} class='{host.CurrentClassName}' levels up={levels}")} ({LastMs:F1} ms)");
            if (host is null) return new(SentenceStatus.NoTextPattern, null, null, 0, double.NaN);
            var tp = (UIA.IUIAutomationTextPattern)host.GetCurrentPattern(UiaIds.TextPattern);

            UIA.IUIAutomationTextRange? caret = null;
            try { caret = Time("RangeFromPoint", () => tp.RangeFromPoint(pt)); W($"    RangeFromPoint: ok ({LastMs:F1} ms) text='{Esc(caret.GetText(50))}' rects={UiaPointReader.ToDoubles(caret.GetBoundingRectangles()).Length / 4}"); }
            catch (Exception ex) { W($"    RangeFromPoint: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"); }

            if (caret is not null && detailed)
            {
                foreach (var unit in new[] { UIA.TextUnit.TextUnit_Character, UIA.TextUnit.TextUnit_Word, UIA.TextUnit.TextUnit_Line, UIA.TextUnit.TextUnit_Paragraph })
                {
                    var c = caret.Clone();
                    Time($"Expand({unit})", () => { c.ExpandToEnclosingUnit(unit); return 0; }); double e = LastMs;
                    string txt = Time($"GetText({unit})", () => c.GetText(4000)); double g = LastMs;
                    var rc = Time($"GetBoundingRectangles({unit})", () => UiaPointReader.ToDoubles(c.GetBoundingRectangles())); double b = LastMs;
                    double dist = UiaPointReader.DistanceToRects(rc, x, y);
                    W($"    {unit,-20} expand={e,6:F1} getText={g,6:F1} rects={b,6:F1} ms | len={txt.Length,4} nRects={rc.Length / 4} dist={dist:F1}px | '{Esc(txt, 110)}'");
                    if (unit == UIA.TextUnit.TextUnit_Line || unit == UIA.TextUnit.TextUnit_Word)
                        for (int i = 0; i + 3 < rc.Length; i += 4) W($"        rect x={rc[i]:F0} y={rc[i + 1]:F0} w={rc[i + 2]:F0} h={rc[i + 3]:F0}");
                }
            }

            _ctx = saved + "-final";
            long t0 = Stopwatch.GetTimestamp();
            var res = R.GetSentenceAtPoint(x, y);
            double total = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Timings.Add(($"{saved}/GetSentenceAtPoint TOTAL", total));
            W($"    GetSentenceAtPoint: status={res.Status} offset={res.Offset} dist={res.DistancePx:F1}px total={total:F1} ms{(res.Error is null ? "" : " error=" + res.Error)}");
            W($"    SENTENCE: {res.Sentence ?? "<none>"}");
            return res;
        }
        catch (Exception ex)
        {
            W($"    EXCEPTION {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return new(SentenceStatus.Error, null, null, 0, double.NaN, ex.Message);
        }
        finally { _ctx = saved; }
    }

    static void PrintDescription(string label, ElementDescription? d)
    {
        if (d is null) { W($"    {label}: <null>"); return; }
        W($"    {label}: ct={d.ControlType} ({d.LocalizedControlType}) class='{d.ClassName}' fw={d.FrameworkId} aid='{d.AutomationId}' pid={d.ProcessId} rect=({d.Left},{d.Top},{d.Width}x{d.Height})");
        W($"        Name='{Esc(d.Name)}'");
        W($"        HelpText='{Esc(d.HelpText, 300)}'");
        W($"        FullDescription='{Esc(d.FullDescription, 300)}'");
        W($"        Value(hasPattern={d.HasValuePattern})='{Esc(d.Value)}' ItemStatus='{Esc(d.ItemStatus)}' AcceleratorKey='{d.AcceleratorKey}' AccessKey='{d.AccessKey}'");
        W($"        Legacy: Name='{Esc(d.LegacyName)}' Description='{Esc(d.LegacyDescription, 300)}' Help='{Esc(d.LegacyHelp, 300)}' Value='{Esc(d.LegacyValue)}' DefaultAction='{d.LegacyDefaultAction}' Shortcut='{d.LegacyShortcut}' Role=0x{d.LegacyRole:X}");
        W($"        => SpokenText='{Esc(d.SpokenText)}'");
    }

    static void RibbonProbe(IntPtr hwnd, string[] buttonNames, string[] tabNames, string prefix)
    {
        // separate client with a generous timeout: a full-tree dump is NOT something the app should ever do
        var big = new UiaPointReader(2000, 20000);
        var a = big.Automation;
        var root = a.ElementFromHandle(hwnd);
        var cr = a.CreateCacheRequest();
        foreach (int id in new[] { UiaIds.Name, UiaIds.ControlType, UiaIds.BoundingRectangle, UiaIds.ClassName, 30022 /*IsOffscreen*/, UiaIds.HelpText })
            cr.AddProperty(id);
        cr.TreeFilter = a.RawViewCondition;
        // only the NetUI part (ribbon + status bar): children of the top window except the document pane
        var all = new List<UIA.IUIAutomationElement>();
        var kids = root.FindAll(UIA.TreeScope.TreeScope_Children, a.CreateTrueCondition());
        W($"top-level window children: {kids.Length}");
        for (int i = 0; i < kids.Length; i++)
        {
            var k = kids.GetElement(i);
            string cls = k.CurrentClassName ?? ""; string nm = k.CurrentName ?? "";
            W($"   [{i}] ct={k.CurrentControlType} class='{cls}' Name='{Esc(nm, 50)}'");
            if (cls is "_WwF" or "_WwG" or "XLDESK" or "EXCEL7") continue;       // document area: skip
            try
            {
                var arr = Time($"FindAllBuildCache(descendants of {cls})", () => k.FindAllBuildCache(UIA.TreeScope.TreeScope_Subtree, a.CreateTrueCondition(), cr));
                W($"       descendants: {arr.Length} in {LastMs:F0} ms");
                for (int j = 0; j < arr.Length; j++) all.Add(arr.GetElement(j));
            }
            catch (Exception ex) { W($"       FindAllBuildCache failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"); }
        }

        using (var tree = new StreamWriter(Path.Combine(_out, $"{prefix}-netui-tree.txt"), false, new UTF8Encoding(false)))
            foreach (var e in all)
            {
                var rc = e.CachedBoundingRectangle;
                tree.WriteLine($"ct={e.CachedControlType} off={e.GetCachedPropertyValue(30022)} class='{e.CachedClassName}' rect=({rc.left},{rc.top},{rc.right - rc.left}x{rc.bottom - rc.top}) Name='{Esc(e.CachedName, 80)}' Help.len={(e.CachedHelpText ?? "").Length}");
            }

        bool Visible(UIA.IUIAutomationElement e) { var rc = e.CachedBoundingRectangle; return rc.right > rc.left && rc.bottom > rc.top && e.GetCachedPropertyValue(30022) is bool off && !off; }
        var clickable = all.Where(e => Visible(e) && e.CachedControlType is UiaIds.Button or UiaIds.CheckBox or UiaIds.SplitButton or 50011 or 50003 or UiaIds.TabItem).ToList();
        int unnamed = clickable.Count(e => string.IsNullOrWhiteSpace(e.CachedName));
        W($"visible NetUI elements of type Button/CheckBox/SplitButton/MenuItem/ComboBox/TabItem: {clickable.Count}; with EMPTY Name: {unnamed}; with HelpText: {clickable.Count(e => !string.IsNullOrEmpty(e.CachedHelpText))}");
        foreach (var e in clickable.Where(e => string.IsNullOrWhiteSpace(e.CachedName)).Take(10))
        { var rc = e.CachedBoundingRectangle; W($"   unnamed: ct={e.CachedControlType} class='{e.CachedClassName}' rect=({rc.left},{rc.top},{rc.right - rc.left}x{rc.bottom - rc.top})"); }

        void AtCentre(string what, UIA.IUIAutomationElement e)
        {
            var rc = e.CachedBoundingRectangle;
            int x = (rc.left + rc.right) / 2, y = (rc.top + rc.bottom) / 2;
            W(); W($"--- {what}: tree element ct={e.CachedControlType} Name='{Esc(e.CachedName, 60)}' rect=({rc.left},{rc.top},{rc.right - rc.left}x{rc.bottom - rc.top}) -> ElementFromPoint({x},{y})");
            Marks.Add((what.Length > 3 ? what[..3] : what, x, y));
            long t0 = Stopwatch.GetTimestamp();
            var d = R.DescribeElementAtPoint(x, y);
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Timings.Add(($"{_ctx}/DescribeElementAtPoint TOTAL", ms));
            PrintDescription($"DescribeElementAtPoint ({ms:F1} ms)", d);
            try
            {
                var hit = R.Automation.ElementFromPoint(new UIA.tagPOINT { x = x, y = y });
                var par = R.Automation.RawViewWalker.GetParentElement(hit);
                W($"        parent: ct={par?.CurrentControlType} class='{par?.CurrentClassName}' Name='{Esc(par?.CurrentName, 60)}'");
            }
            catch (Exception ex) { W("        parent: " + ex.Message); }
        }

        int found = 0;
        foreach (var name in buttonNames)
        {
            var e = clickable.FirstOrDefault(c => (c.CachedName ?? "").StartsWith(name, StringComparison.CurrentCultureIgnoreCase));
            if (e is null) { W($"(no visible ribbon element whose Name starts with '{name}')"); continue; }
            AtCentre($"B{++found} ribbon '{name}'", e);
        }
        foreach (var name in tabNames)
        {
            var e = all.FirstOrDefault(c => c.CachedControlType == UiaIds.TabItem && Visible(c) && (c.CachedName ?? "").StartsWith(name, StringComparison.CurrentCultureIgnoreCase));
            if (e is null) { W($"(no TabItem '{name}')"); continue; }
            AtCentre($"TAB '{name}'", e);
        }
        var sb = all.FirstOrDefault(c => c.CachedControlType == UiaIds.StatusBar && Visible(c));
        if (sb is null) W("(no StatusBar element found)");
        else
        {
            var sr = sb.CachedBoundingRectangle;
            W($"StatusBar: Name='{Esc(sb.CachedName)}' rect=({sr.left},{sr.top},{sr.right - sr.left}x{sr.bottom - sr.top})");
            var items = all.Where(c => Visible(c) && !ReferenceEquals(c, sb) && c.CachedControlType != UiaIds.StatusBar && c.CachedControlType != UiaIds.Pane
                                       && c.CachedBoundingRectangle.top >= sr.top && c.CachedBoundingRectangle.bottom <= sr.bottom
                                       && c.CachedBoundingRectangle.left >= sr.left && c.CachedBoundingRectangle.right <= sr.right
                                       && !string.IsNullOrWhiteSpace(c.CachedName)).ToList();
            W($"status bar named items: {string.Join(" | ", items.Select(i => $"{i.CachedControlType}:'{Esc(i.CachedName, 40)}'"))}");
            foreach (var it in items.Take(3)) AtCentre($"SB '{Esc(it.CachedName, 25)}'", it);
        }
    }

    // =====================================================================================  EXCEL

    static void RunExcel()
    {
        _ctx = "excel";
        var before = Pids("EXCEL");
        W($"EXCEL PIDs before: [{string.Join(",", before)}]; WINWORD: [{string.Join(",", Pids("WINWORD"))}]");
        dynamic? app = null, wb = null;
        bool mine = false;
        try
        {
            var type = Type.GetTypeFromProgID("Excel.Application") ?? throw new InvalidOperationException("Excel.Application not registered");
            app = Time("COM CreateInstance(Excel.Application)", () => Activator.CreateInstance(type)!);
            W($"CreateInstance: {LastMs:F0} ms");
            Thread.Sleep(500);
            var created = Pids("EXCEL").Except(before).ToList();
            W($"created EXCEL PIDs: [{string.Join(",", created)}]");
            if (created.Count != 1) { W("ABORT: could not identify exactly one NEW Excel process."); return; }
            _myPid = created[0];
            File.WriteAllText(Path.Combine(_out, "my-excel-pid.txt"), _myPid.ToString());
            IntPtr hwnd = new IntPtr((int)app.Hwnd);
            Native.GetWindowThreadProcessId(hwnd, out uint hp);
            W($"My Excel PID={_myPid} machine={Machine(_myPid)} app.Hwnd=0x{hwnd:X} class={Native.ClassOf(hwnd)} pid={hp} Version={app.Version} Build={app.Build}");
            if (hp != _myPid) { W("ABORT: hwnd PID mismatch"); return; }
            mine = true;

            Retry("DisplayAlerts", () => app.DisplayAlerts = false);
            Retry("Workbooks.Add", () => wb = app.Workbooks.Add());
            Retry("Visible", () => app.Visible = true);
            Retry("WindowState", () => app.WindowState = -4143);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 60, 20, 1800, 1100, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            dynamic ws = wb!.ActiveSheet;
            dynamic win = app.ActiveWindow;

            Retry("fill", () =>
            {
                ws.Range("B2").Value2 = "Prodotto"; ws.Range("C2").Value2 = "Quantità"; ws.Range("D2").Value2 = "Prezzo";
                ws.Range("B3").Value2 = "Mele rosse"; ws.Range("C3").Value2 = 1234.5; ws.Range("D3").Value2 = 1.5;
                ws.Range("B4").Value2 = "Totale"; ws.Range("C4").Value2 = 7; ws.Range("D4").Formula = "=C3*D3";
                ws.Range("C3").NumberFormat = "#,##0.00";
                ws.Range("D3:D4").NumberFormat = "#,##0.00 \"€\"";
                ws.Range("B2:D2").Font.Bold = true;
                ws.Range("A1").Select();
            });
            Thread.Sleep(2500);
            uint dpi = Native.GetDpiForWindow(hwnd);
            double zoom = (double)win.Zoom;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint fg);
            W($"DPI={dpi} zoom={zoom}% foreground pid={fg} (mine={_myPid})");

            int x0 = (int)win.PointsToScreenPixelsX(0), y0 = (int)win.PointsToScreenPixelsY(0);
            W($"PointsToScreenPixelsX(0)={x0} PointsToScreenPixelsY(0)={y0}");
            double f = dpi / 72.0 * zoom / 100.0;

            (int x, int y, int w, int h) CellRect(string addr)
            {
                dynamic c = ws.Range(addr);
                double l = (double)c.Left, t = (double)c.Top, cw = (double)c.Width, ch = (double)c.Height;
                return ((int)Math.Round(x0 + l * f), (int)Math.Round(y0 + t * f), (int)Math.Round(cw * f), (int)Math.Round(ch * f));
            }

            W(); W("########## 5. EXCEL CELLS ##########");
            {
                dynamic c3 = ws.Range("C3");
                int apiX = (int)win.PointsToScreenPixelsX((double)c3.Left), apiY = (int)win.PointsToScreenPixelsY((double)c3.Top);
                var cr3 = CellRect("C3");
                W($"C3 points: Left={(double)c3.Left} Top={(double)c3.Top} Width={(double)c3.Width} Height={(double)c3.Height}");
                W($"C3 via PointsToScreenPixelsX/Y(Left/Top) directly = ({apiX},{apiY}); via x0 + Left*dpi/72*zoom = ({cr3.x},{cr3.y}) size {cr3.w}x{cr3.h}");
            }

            foreach (string addr in new[] { "C3", "B2", "B3", "D3", "C4", "D4", "E6" })
            {
                _ctx = "excel-cell";
                var r = CellRect(addr);
                int cx = r.x + r.w / 2, cy = r.y + r.h / 2;
                dynamic c = ws.Range(addr);
                string comAtPoint = "?";
                try { dynamic rp = Time("COM Window.RangeFromPoint", () => win.RangeFromPoint(cx, cy)); comAtPoint = rp is null ? "<null>" : (string)rp.Address; } catch (Exception ex) { comAtPoint = "ERR " + ex.Message; }
                W(); W($"--- cell {addr}: calc rect=({r.x},{r.y},{r.w}x{r.h}) centre=({cx},{cy}); COM RangeFromPoint(centre)={comAtPoint}; COM Text='{(string)c.Text}' Formula='{(string)c.Formula}' FormulaLocal='{(string)c.FormulaLocal}' Value2='{c.Value2}'");
                Marks.Add((addr, cx, cy));
                var np = new Native.POINT { x = cx, y = cy }; var hw = Native.WindowFromPoint(np); Native.GetWindowThreadProcessId(hw, out uint pid);
                W($"    WindowFromPoint class={Native.ClassOf(hw)} pid={pid}");
                if (pid != _myPid) { W("    !! not my window"); continue; }
                long t0 = Stopwatch.GetTimestamp();
                var d = R.DescribeElementAtPoint(cx, cy);
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds; Timings.Add(("excel-cell/DescribeElementAtPoint TOTAL", ms));
                PrintDescription($"DescribeElementAtPoint ({ms:F1} ms)", d);
                if (addr == "C3") Ancestors(cx, cy);
            }

            W(); W("########## 5b. HEADERS, FORMULA BAR, NAME BOX ##########");
            _ctx = "excel-ui";
            Retry("select D4", () => ws.Range("D4").Select());
            Thread.Sleep(500);
            {
                var c = CellRect("C3"); var d4 = CellRect("D4");
                int cx = c.x + c.w / 2;
                int headerY = y0 - (int)(10 * dpi / 96.0);
                W(); W($"--- column header above C: ({cx},{headerY})"); Marks.Add(("colC", cx, headerY));
                PrintDescription("column header", R.DescribeElementAtPoint(cx, headerY));
                int rowX = x0 - (int)(12 * dpi / 96.0), cy = c.y + c.h / 2;
                W(); W($"--- row header left of row 3: ({rowX},{cy})"); Marks.Add(("row3", rowX, cy));
                PrintDescription("row header", R.DescribeElementAtPoint(rowX, cy));
                W(); W($"--- pointing at the ACTIVE cell D4 (formula) after Select");
                PrintDescription("active cell D4", R.DescribeElementAtPoint(d4.x + d4.w / 2, d4.y + d4.h / 2));

                W(); W("--- scanning upwards from the column header to find the formula bar");
                string last = "";
                for (int y = headerY - 10; y > headerY - (int)(90 * dpi / 96.0); y -= (int)(8 * dpi / 96.0))
                {
                    var d = R.DescribeElementAtPoint(cx + 300, y);
                    if (d is null) continue;
                    string key = $"{d.ControlType}|{d.Name}|{d.ClassName}|{d.AutomationId}";
                    if (key == last) continue; last = key;
                    W($"  y={y}:"); Marks.Add(("fb", cx + 300, y));
                    PrintDescription("scan", d);
                }
                try
                {
                    var fb = A.ElementFromPoint(new UIA.tagPOINT { x = cx + 300, y = headerY - 20 });
                    W($"formula bar element: ct={fb.CurrentControlType} aid='{fb.CurrentAutomationId}' Name='{Esc(fb.CurrentName, 40)}' textPattern={fb.GetCurrentPropertyValue(UiaIds.IsTextPatternAvailable)} valuePattern={fb.GetCurrentPropertyValue(UiaIds.IsValuePatternAvailable)}");
                    if (fb.GetCurrentPattern(UiaIds.TextPattern) is UIA.IUIAutomationTextPattern ftp)
                    {
                        string ft = Time("FormulaBar.DocumentRange.GetText", () => ftp.DocumentRange.GetText(2000));
                        W($"formula bar TextPattern.DocumentRange.GetText = '{Esc(ft)}' ({LastMs:F1} ms) [active cell D4, COM Formula='{(string)ws.Range("D4").Formula}']");
                    }
                    var fs = R.GetSentenceAtPoint(cx + 300, headerY - 20);
                    W($"GetSentenceAtPoint on the formula bar: status={fs.Status} sentence='{Esc(fs.Sentence)}' dist={fs.DistancePx:F1}");
                }
                catch (Exception ex) { W($"formula bar read: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"); }

                W(); W("--- name box area (left of formula bar)");
                for (int y = headerY - 10; y > headerY - (int)(90 * dpi / 96.0); y -= (int)(8 * dpi / 96.0))
                {
                    var d = R.DescribeElementAtPoint(x0 + 60, y);
                    if (d is null) continue;
                    string key = $"{d.ControlType}|{d.Name}|{d.ClassName}|{d.AutomationId}";
                    if (key == last) continue; last = key;
                    W($"  y={y}:");
                    PrintDescription("scan", d);
                }
            }

            W(); W("########## 5c. EXCEL SELECTION / FOCUS ##########");
            _ctx = "excel-sel";
            Retry("select B2:D4", () => ws.Range("B2:D4").Select());
            Thread.Sleep(500);
            try
            {
                var fe = Time("GetFocusedElement", () => A.GetFocusedElement());
                PrintDescription("focused element", R.Describe(fe));
            }
            catch (Exception ex) { W("GetFocusedElement: " + ex.Message); }
            try
            {
                var c = CellRect("C3");
                var el = A.ElementFromPoint(new UIA.tagPOINT { x = c.x + c.w / 2, y = c.y + c.h / 2 });
                var walker = A.RawViewWalker; UIA.IUIAutomationElement? cur = el;
                for (int i = 0; i < 6 && cur is not null; i++)
                {
                    if (cur.GetCurrentPattern(UiaIds.SelectionPattern) is UIA.IUIAutomationSelectionPattern sp)
                    {
                        var sel = Time("SelectionPattern.GetCurrentSelection", () => sp.GetCurrentSelection());
                        W($"SelectionPattern on ct={cur.CurrentControlType} '{Esc(cur.CurrentName, 40)}': {sel.Length} item(s) ({LastMs:F1} ms)");
                        for (int k = 0; k < Math.Min(sel.Length, 12); k++)
                        {
                            var s = sel.GetElement(k);
                            string v = s.GetCurrentPattern(UiaIds.ValuePattern) is UIA.IUIAutomationValuePattern vp ? vp.CurrentValue : "<no ValuePattern>";
                            W($"    sel[{k}] ct={s.CurrentControlType} Name='{Esc(s.CurrentName)}' Value='{Esc(v)}'");
                        }
                        break;
                    }
                    cur = walker.GetParentElement(cur);
                }
                var tpHost = R.FindTextHost(el, out int lv);
                W($"TextPattern host above a cell: {(tpHost is null ? "NONE" : $"ct={tpHost.CurrentControlType} class={tpHost.CurrentClassName} levels={lv}")}");
                var sres = R.GetSentenceAtPoint(c.x + c.w / 2, c.y + c.h / 2);
                W($"GetSentenceAtPoint on a cell: status={sres.Status} sentence='{Esc(sres.Sentence)}' err={sres.Error}");
            }
            catch (Exception ex) { W("selection probe: " + ex.GetType().Name + " " + ex.Message); }

            W(); W("########## 5d. EXCEL RIBBON / STATUS BAR ##########");
            _ctx = "excel-ribbon";
            RibbonProbe(hwnd, new[] { "Grassetto", "Somma automatica", "Unisci" }, new[] { "Formule" }, "excel");

            SaveScreenshot(hwnd, "excel-window.png");
        }
        finally
        {
            _ctx = "excel-cleanup";
            if (app is not null)
            {
                if (mine)
                {
                    try { if (wb is not null) { wb.Saved = true; wb.Close(false); } } catch (Exception ex) { W("wb.Close: " + ex.Message); }
                    try { if (wb is not null) Marshal.FinalReleaseComObject(wb); } catch { }
                    wb = null;
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers();
                    W("released workbook/sheet/window/range RCWs before Quit");
                    try { app.Quit(); } catch (Exception ex) { W("app.Quit: " + ex.Message); }
                }
                try { if (wb is not null) Marshal.FinalReleaseComObject(wb); } catch { }
                try { Marshal.FinalReleaseComObject(app); } catch { }
                wb = null; app = null;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            VerifyGone("EXCEL");
        }
    }

    static void Ancestors(int x, int y)
    {
        var el = A.ElementFromPoint(new UIA.tagPOINT { x = x, y = y });
        var walker = A.RawViewWalker; UIA.IUIAutomationElement? cur = el;
        W("    ancestors (raw view):");
        for (int i = 0; i < 10 && cur is not null; i++)
        {
            W($"      [{i}] ct={cur.CurrentControlType} ({cur.CurrentLocalizedControlType}) class='{cur.CurrentClassName}' fw={cur.CurrentFrameworkId} aid='{Esc(cur.CurrentAutomationId, 30)}' Name='{Esc(cur.CurrentName, 50)}' textPattern={cur.GetCurrentPropertyValue(UiaIds.IsTextPatternAvailable)}");
            cur = walker.GetParentElement(cur);
        }
    }
}
