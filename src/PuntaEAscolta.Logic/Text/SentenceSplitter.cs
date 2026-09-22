using System.Text;

namespace PuntaEAscolta.Logic.Text;

/// <summary>
/// Spezzatura in frasi con regole italiane. Vedi docs/DESIGN.md sez. 3.1 e docs/research/probe-office.md.
/// I caratteri di controllo che Word inserisce nel testo del paragrafo (\r fine paragrafo, \a fine cella,
/// \v interruzione di riga, \f interruzione di pagina, U+FFFC oggetto) valgono come fine frase forte e vengono
/// rimossi SOLO dalla frase già estratta, così gli indici restano quelli del testo originale.
/// </summary>
public static class SentenceSplitter
{
    /// <summary>Abbreviazioni italiane comuni (senza il punto finale, minuscole). Il punto che le segue non chiude la frase.</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "sig", "sigg", "sig.ra", "sig.na", "sigra", "dott", "dott.ssa", "dr", "dr.ssa", "prof", "prof.ssa", "ing", "avv", "arch", "geom", "rag",
        "on", "sen", "mons", "rev", "gen", "col", "cap", "ten", "magg", "comm", "cav",
        "ecc", "etc", "es", "ad es", "p.es", "cfr", "vd", "v", "vs", "pag", "pagg", "p", "pp", "n", "nn", "num", "art", "artt", "cc", "c.p", "c.c", "co", "par", "cap", "capp", "vol", "voll", "fig", "figg", "tab", "all", "lett",
        "tel", "fax", "cell", "e-mail", "c.a", "c.c.p", "p.iva", "c.f", "s.p.a", "s.r.l", "s.n.c", "s.a.s", "s.s", "soc", "spa", "srl",
        "via", "v.le", "p.zza", "p.le", "c.so", "loc", "fraz", "prov", "reg", "naz", "int", "sc", "fr",
        "gen", "feb", "mar", "apr", "mag", "giu", "lug", "ago", "set", "sett", "ott", "nov", "dic",
        "lun", "mart", "merc", "giov", "ven", "sab", "dom",
        "a.c", "d.c", "a.m", "p.m", "ca", "circa", "max", "min", "sec", "h", "km", "kg", "mq", "mc", "cm", "mm", "ml", "gr", "lt",
        "mr", "mrs", "ms", "st", "jr", "sr", "inc", "ltd", "co", "corp", "dept", "no", "vol", "approx",
    };

    /// <summary>
    /// Abbreviazioni che sono anche parole comuni o che chiudono spesso una frase ("circa.", "via.", "no.", "5 min.", "ecc.",
    /// giorni della settimana): il punto chiude la frase se la parola dopo comincia con una maiuscola ("alle 15.45 circa. Vedi pag. 12").
    /// Davanti a cifre o minuscole resta un'abbreviazione ("ecc. ma non importa"). Le altre voci della lista
    /// (titoli come "sig.", "dott.", "col."... e rimandi come "pag.", "art.") precedono un nome o un numero: non chiudono mai.
    /// </summary>
    private static readonly HashSet<string> AmbiguousAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "circa", "ca", "via", "no", "min", "max", "sec", "h", "km", "kg", "mq", "mc", "cm", "mm", "ml", "gr", "lt",
        "ecc", "etc", "approx", "inc", "ltd", "corp", "co", "dept", "jr", "sr", "spa", "srl", "soc",
        "lun", "mart", "merc", "giov", "ven", "sab", "dom", "fax",
    };

    /// <summary>
    /// Mesi abbreviati: ambigui (fine frase davanti a una maiuscola) SOLO subito dopo il numero del giorno
    /// ("il 10 gen. Porta i documenti"); altrimenti sono titoli o parole che precedono un nome e non chiudono mai
    /// ("il gen. Rossi", generale; "3 mar. 2026" resta intero perché dopo c'è una cifra).
    /// </summary>
    private static readonly HashSet<string> MonthAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "gen", "feb", "mar", "apr", "mag", "giu", "lug", "ago", "set", "sett", "ott", "nov", "dic",
    };

    /// <summary>Restituisce la frase di <paramref name="paragraph"/> che contiene il carattere in posizione <paramref name="offset"/>, già ripulita dai caratteri di controllo.</summary>
    public static string ExtractSentence(string paragraph, int offset)
    {
        if (string.IsNullOrEmpty(paragraph)) return string.Empty;
        offset = Math.Clamp(offset, 0, paragraph.Length - 1);

        var ranges = SentenceRanges(paragraph);
        if (ranges.Count == 0) return Clean(paragraph);

        // Se il punto cade su spazi fra due frasi, si preferisce la frase successiva.
        (int start, int end) chosen = ranges[^1];
        for (int i = 0; i < ranges.Count; i++)
        {
            var (s, e) = ranges[i];
            if (offset < s) { chosen = ranges[i]; break; }
            if (offset < e) { chosen = ranges[i]; break; }
        }
        return Clean(paragraph.AsSpan(chosen.start, chosen.end - chosen.start));
    }

    /// <summary>Spezza un testo in frasi; le frasi più lunghe di <paramref name="maxChunkChars"/> vengono divise su punto e virgola, virgola o spazio.</summary>
    public static IReadOnlyList<string> SplitSentences(string text, int maxChunkChars = 400)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        if (maxChunkChars < 20) maxChunkChars = 20;

        foreach (var (s, e) in SentenceRanges(text))
        {
            var sentence = Clean(text.AsSpan(s, e - s));
            if (sentence.Length == 0) continue;
            if (sentence.Length <= maxChunkChars) { result.Add(sentence); continue; }
            result.AddRange(SplitLong(sentence, maxChunkChars));
        }
        return result;
    }

    /// <summary>Indici [inizio, fine) delle frasi nel testo, esclusi gli spazi iniziali; la punteggiatura di chiusura è inclusa.</summary>
    internal static List<(int Start, int End)> SentenceRanges(string text)
    {
        var ranges = new List<(int, int)>();
        int n = text.Length, start = 0;
        while (start < n && IsSkippable(text[start])) start++;

        int i = start;
        while (i < n)
        {
            char c = text[i];
            if (IsHardBreak(c))
            {
                AddRange(ranges, text, start, i);
                i++;
                while (i < n && IsSkippable(text[i])) i++;
                start = i;
                continue;
            }
            if (c is '.' or '!' or '?' or '…')
            {
                int j = i;
                // sequenze come "...", "?!", "!!!"
                while (j + 1 < n && text[j + 1] is '.' or '!' or '?' or '…') j++;
                bool ellipsis = j > i && text[i] == '.' && text[j] == '.';
                // chiusure che appartengono alla frase: virgolette e parentesi
                int k = j + 1;
                while (k < n && IsClosing(text[k])) k++;

                if (c == '.' && !ellipsis && !IsSentenceEndDot(text, i)) { i++; continue; }

                bool endOfText = k >= n;
                bool followedBySpace = !endOfText && char.IsWhiteSpace(text[k]);
                bool followedByBreak = !endOfText && IsHardBreak(text[k]);
                if (endOfText || followedByBreak || (followedBySpace && NextIsSentenceStart(text, k)) || (ellipsis && followedBySpace && NextIsUpper(text, k)))
                {
                    AddRange(ranges, text, start, k);
                    i = k;
                    while (i < n && IsSkippable(text[i])) i++;
                    start = i;
                    continue;
                }
                i = k;
                continue;
            }
            i++;
        }
        AddRange(ranges, text, start, n);
        return ranges;
    }

    private static void AddRange(List<(int, int)> ranges, string text, int start, int end)
    {
        while (end > start && IsSkippable(text[end - 1])) end--;
        while (start < end && IsSkippable(text[start])) start++;
        if (end > start) ranges.Add((start, end));
    }

    private static bool IsHardBreak(char c) => c is '\r' or '\n' or '\v' or '\f' or '\a' or (char)0x2028 or (char)0x2029;
    private static bool IsSkippable(char c) => char.IsWhiteSpace(c) || char.IsControl(c) || c is (char)0xFFFC or (char)0x200B or (char)0xFEFF;
    private static bool IsClosing(char c) => c is '"' or '\'' or '»' or '”' or '’' or ')' or ']' or '}' or '›' or '"';

    /// <summary>Decide se il punto in posizione i chiude una frase: no dopo abbreviazioni, iniziali puntate, numeri e sigle.</summary>
    private static bool IsSentenceEndDot(string text, int i)
    {
        // Numero: "15.30", "1.000", "3.5"
        if (i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) return false;

        // Parola che precede il punto
        int w = i;
        while (w > 0 && (char.IsLetterOrDigit(text[w - 1]) || text[w - 1] is '.' or '-' or '\'' or '’')) w--;
        var word = text.AsSpan(w, i - w).TrimStart('.');
        int apos = word.LastIndexOfAny(['\'', '’']);
        if (apos >= 0 && apos < word.Length - 1) word = word[(apos + 1)..]; // "l'ing" -> "ing"

        if (word.Length == 0) return true;

        // Numero con punto finale ("alle 15.30.", "costa 1.000."): il punto chiude la frase
        bool numeric = true;
        foreach (var ch in word) if (!(char.IsDigit(ch) || ch is '.' or ',')) { numeric = false; break; }
        if (numeric) return true;

        // Iniziale puntata: "G. Rossi", "A.B. Rossi"
        if (word.Length == 1 && char.IsLetter(word[0])) return false;
        if (word.Length <= 5 && word.Contains('.') && !word.Contains(' ')) return false; // "S.p.A", "c.c", "p.es"

        // Numero ordinale con punto "1." o elenco "a." seguiti da minuscola: non fine frase
        if (word.Length <= 2 && char.IsDigit(word[0]) && NextIsLower(text, i + 1)) return false;

        string lookup = word.ToString();
        if (MonthAbbreviations.Contains(lookup)) return PrecededByDayNumber(text, i - word.Length) && NextIsUpperLetter(text, i + 1);
        if (AmbiguousAbbreviations.Contains(lookup)) return NextIsUpperLetter(text, i + 1);
        if (Abbreviations.Contains(lookup)) return false;

        // Sigle maiuscole corte tipo "U.S." già gestite sopra; una parola tutta minuscola seguita da minuscola: dubbio, non chiudere
        if (NextIsLower(text, i + 1) && word.Length <= 4 && IsAllLower(word)) return false;
        return true;
    }

    /// <summary>
    /// La parola che comincia in <paramref name="wordStart"/> è preceduta (dopo almeno uno spazio) da un numero di giorno
    /// da 1 a 31, anche con il segno di ordinale ("10 gen.", "1° mag.", "1º mag.").
    /// </summary>
    private static bool PrecededByDayNumber(string text, int wordStart)
    {
        int k = wordStart;
        while (k > 0 && char.IsWhiteSpace(text[k - 1])) k--;
        if (k == wordStart || k == 0) return false;

        int end = k;
        if (text[end - 1] is (char)0x00B0 or (char)0x00BA) end--;
        int start = end;
        while (start > 0 && char.IsAsciiDigit(text[start - 1]) && end - start < 3) start--;
        int digits = end - start;
        if (digits is < 1 or > 2) return false;
        if (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '.' or ',')) return false; // "15.30 gen", "A1 gen"
        int day = int.Parse(text.AsSpan(start, digits), System.Globalization.CultureInfo.InvariantCulture);
        return day is >= 1 and <= 31;
    }

    private static bool IsAllLower(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (char.IsLetter(c) && !char.IsLower(c)) return false;
        return true;
    }

    private static bool NextIsLower(string text, int k)
    {
        while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
        return k < text.Length && char.IsLetter(text[k]) && char.IsLower(text[k]);
    }

    private static bool NextIsUpper(string text, int k)
    {
        while (k < text.Length && (char.IsWhiteSpace(text[k]) || IsOpening(text[k]))) k++;
        return k < text.Length && (char.IsUpper(text[k]) || char.IsDigit(text[k]));
    }

    /// <summary>La parola successiva (dopo spazi e aperture di virgolette o parentesi) comincia con una lettera maiuscola.</summary>
    private static bool NextIsUpperLetter(string text, int k)
    {
        while (k < text.Length && IsClosing(text[k])) k++;
        while (k < text.Length && (char.IsWhiteSpace(text[k]) || IsOpening(text[k]))) k++;
        return k < text.Length && char.IsLetter(text[k]) && char.IsUpper(text[k]);
    }

    private static bool IsOpening(char c) => c is '"' or '«' or '“' or '‘' or '(' or '[' or '‹' or '\'';

    /// <summary>Dopo punteggiatura e spazio: la frase nuova inizia con maiuscola, cifra, virgoletta o parentesi aperta, oppure con un carattere non lettera (emoji, trattino di elenco).</summary>
    private static bool NextIsSentenceStart(string text, int k)
    {
        while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
        if (k >= text.Length) return true;
        char c = text[k];
        if (char.IsLetter(c)) return char.IsUpper(c);
        return true; // cifra, virgoletta, parentesi, simbolo
    }

    private static string Clean(ReadOnlySpan<char> span)
    {
        var sb = new StringBuilder(span.Length);
        bool pendingSpace = false;
        foreach (var c in span)
        {
            bool isSpace = char.IsWhiteSpace(c) || char.IsControl(c) || c is (char)0xFFFC or (char)0x200B or (char)0xFEFF or (char)0x00A0;
            if (isSpace) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Clean(string s) => Clean(s.AsSpan());

    private static IEnumerable<string> SplitLong(string sentence, int max)
    {
        var rest = sentence.AsMemory();
        while (rest.Length > max)
        {
            var span = rest.Span[..max];
            int cut = LastIndexOfAny(span, ";:") + 1;
            if (cut < max / 3) cut = span.LastIndexOf(',') + 1;
            if (cut < max / 3) cut = span.LastIndexOf(' ');
            if (cut <= 0) cut = max;
            var piece = rest[..cut].ToString().Trim();
            if (piece.Length > 0) yield return piece;
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0) yield return rest.ToString().Trim();
    }

    private static int LastIndexOfAny(ReadOnlySpan<char> span, string chars)
    {
        for (int i = span.Length - 1; i >= 0; i--) if (chars.Contains(span[i])) return i;
        return -1;
    }
}
