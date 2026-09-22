using System.Text.RegularExpressions;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Logic.Text;

/// <summary>
/// Pulizia delle etichette dell'interfaccia e delle righe OCR prima della pronuncia. Vedi docs/DESIGN.md sez. 3.1
/// e le misure in docs/research/probe-affinity.md (scorciatoie come "CtrI+N", "Ctr1", "Ctd", "AIt", "Ctrl + p").
/// </summary>
public static partial class LabelCleaner
{
    // Modificatori come li scrive l'interfaccia e come li storpia l'OCR (Ctrl -> CtrI, Ctr1, Ctd, Cmd; Alt -> AIt, A1t; Maiusc/Shift; Win).
    private const string Modifier = @"(?:C[tT][rR][lI1d]|C[tT][lI1]|Ctd|Ctr|Control|Strg|A[lI1][tT]|AItGr|AltGr|Ma[iíì]?usc(?:ol[eo])?|Sh[iíì]ft|Sh[iíì]f|W[iíì]n(?:dows)?|Cmd|Comando|Opzione|Option|Fn|Meta|Super|⌘|⌥|⇧|⌃)";
    // Tasto finale: lettera, cifra, tasto funzione, nomi di tasti, simboli.
    private const string Key = @"(?:F[1-9]|F1[0-9]|F2[0-4]|[A-Za-z0-9]{1,2}|Spazio|Space|Invio|Enter|Return|Esc|Escape|Tab|Canc|Del(?:ete)?|Ins(?:ert)?|Backspace|Home|Fine|End|PagSu|PagGiù|PagGiu|PgUp|PgDn|Page ?Up|Page ?Down|Su|Giù|Giu|Sinistra|Destra|Left|Right|Up|Down|Freccia \w+|Num ?[0-9]|[+\-*/=,.;'\\\[\]`<>~^°§|])";

    /// <summary>Scorciatoia intera: uno o più modificatori uniti da +, -, spazi, poi un tasto; oppure un tasto funzione da solo (F2, F12).</summary>
    [GeneratedRegex(@"^\s*(?:(?:" + Modifier + @")\s*[+\-]?\s*)+(?:" + Key + @")?\s*[/\\]?\s*$|^\s*F(?:[1-9]|1[0-9]|2[0-4])\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WholeShortcut();

    /// <summary>Scorciatoia in coda a un'etichetta: separata da tabulazione o da almeno due spazi, oppure da uno spazio se inizia con un modificatore.</summary>
    [GeneratedRegex(@"(?:\t+|\s{2,}|\s+(?=" + Modifier + @"\s*[+\-]))\s*(?:(?:" + Modifier + @")\s*[+\-]?\s*)+(?:" + Key + @")?\s*[/\\]?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TrailingShortcut();

    /// <summary>Tasto funzione isolato in coda ("Guida  F1", "Rinomina\tF2").</summary>
    [GeneratedRegex(@"(?:\t+|\s{2,})F(?:[1-9]|1[0-9]|2[0-4])\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingFunctionKey();

    /// <summary>Marcatore di tasto di accesso Win32/WinForms/WPF: "&amp;File", "_File", "File(&amp;F)", "File (_F)".</summary>
    [GeneratedRegex(@"\s*\((?:&|_)[^\s()]\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex AccessKeyInParens();

    [GeneratedRegex(@"(?<!&)&(?!&)(?=\S)", RegexOptions.CultureInvariant)]
    private static partial Regex AmpersandMarker();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])_(?=\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex UnderscoreMarker();

    /// <summary>Puntini di sospensione finali dei comandi che aprono una finestra ("Apri...", "Salva con nome…").</summary>
    [GeneratedRegex(@"\s*(?:\.{2,}|…)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDots();

    /// <summary>Frecce e simboli di sottomenu, spunta, elenco a inizio o fine riga.</summary>
    [GeneratedRegex(@"^[\s▸▶►▷➤➜→›»>✓✔☑☐•·◦▪▫■□○●\-–—|~*]+|[\s▸▶►▷➤➜→›»>|~]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EdgeGlyphs();

    /// <summary>Riga OCR: un simbolo isolato a inizio riga seguito da spazio (icona letta come lettera: "v Layout", "> Esporta", "I Nuovo").</summary>
    [GeneratedRegex(@"^(?:[^\p{L}\p{N}\s]|[vVIl|>])\s+(?=\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingStrayGlyph();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpace();

    /// <summary>Etichetta proveniente dall'accessibilità (marcatori di tasto di accesso, scorciatoie in coda, puntini, frecce).</summary>
    public static string Clean(string raw, UiElementKind kind, ReadingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Replace((char)0x00A0, ' ').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();

        s = AccessKeyInParens().Replace(s, "");
        s = AmpersandMarker().Replace(s, "");
        s = s.Replace("&&", "&");
        if (kind is UiElementKind.MenuItem or UiElementKind.Menu or UiElementKind.MenuBar or UiElementKind.Button or UiElementKind.TabItem
            or UiElementKind.CheckBox or UiElementKind.RadioButton or UiElementKind.ListItem or UiElementKind.Text or UiElementKind.Unknown or UiElementKind.Custom)
        {
            s = UnderscoreMarker().Replace(s, "");
        }

        if (settings.StripKeyboardShortcuts)
        {
            s = TrailingShortcut().Replace(s, "");
            s = TrailingFunctionKey().Replace(s, "");
        }
        s = TrailingDots().Replace(s, "");
        s = EdgeGlyphs().Replace(s, "");
        s = MultiSpace().Replace(s.Replace('\t', ' '), " ").Trim();

        if (settings.StripEmoji) s = EmojiFilter.Strip(s);
        return s;
    }

    /// <summary>Riga proveniente dall'OCR (scorciatoia a destra, simboli spuri isolati a inizio riga).</summary>
    public static string CleanOcrLine(string line, ReadingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;
        var s = line.Replace((char)0x00A0, ' ').Trim();
        s = MultiSpace().Replace(s, " ");

        if (settings.StripKeyboardShortcuts)
        {
            if (IsKeyboardShortcut(s)) return string.Empty;
            s = TrailingShortcut().Replace(s, "");
            s = TrailingFunctionKey().Replace(s, "");
            // Scorciatoia separata da un solo spazio in fondo alla riga ("Nuovo Ctrl+N"): si toglie l'ultimo token se è una scorciatoia
            int lastSpace = s.LastIndexOf(' ');
            while (lastSpace > 0 && IsKeyboardShortcut(s[(lastSpace + 1)..]) && s[(lastSpace + 1)..].Contains('+'))
            {
                s = s[..lastSpace].TrimEnd();
                lastSpace = s.LastIndexOf(' ');
            }
        }
        s = LeadingStrayGlyph().Replace(s, "");
        s = TrailingDots().Replace(s, "");
        s = EdgeGlyphs().Replace(s, "");
        s = s.Trim();

        // Riga fatta solo di simboli o di un solo carattere non alfanumerico: rumore
        if (s.Length == 0 || !s.Any(char.IsLetterOrDigit)) return string.Empty;
        if (s.Length == 1 && !char.IsLetterOrDigit(s[0])) return string.Empty;

        if (settings.StripEmoji) s = EmojiFilter.Strip(s);
        return s;
    }

    /// <summary>True se il testo è, con tolleranza agli errori OCR, una scorciatoia da tastiera come "Ctrl+N", "CtrI + Maiusc + S", "Alt+F4", "F5".</summary>
    public static bool IsKeyboardShortcut(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Length > 40) return false;
        return WholeShortcut().IsMatch(t);
    }
}
