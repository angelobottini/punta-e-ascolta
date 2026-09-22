using System.Text;

namespace PuntaEAscolta.Ocr.Onnx;

/// <summary>
/// Filtro dei caratteri per la sintesi vocale: tiene solo lettere latine, cifre, punteggiatura e pochi simboli
/// comuni (€, °, %, ™). Scarta cirillico, frecce, forme geometriche, operatori matematici, segni di spunta e il
/// carattere di sostituzione, che il dizionario PP-OCRv5 latin può emettere sul rumore. Converte legature, numeri
/// romani, numeri cerchiati e le lettere greche uguali a quelle latine; scarta le altre lettere greche.
/// </summary>
public static class LatinTextFilter
{
    /// <summary>Restituisce la forma "pronunciabile" di un singolo carattere (o legatura) del riconoscitore, oppure null se va scartato.</summary>
    public static string? MapCharacter(string ch)
    {
        if (string.IsNullOrEmpty(ch))
        {
            return null;
        }

        // Legature e sostituzioni esplicite prima del filtro per intervalli. I caratteri sono indicati per codice
        // (mai letterali invisibili o sequenze di escape nel sorgente).
        if (ch.Length == 1 && MapSingle(ch[0], out string? replacement))
        {
            return replacement;
        }

        var sb = new StringBuilder(ch.Length);
        foreach (var rune in ch.EnumerateRunes())
        {
            if (IsAllowed(rune.Value))
            {
                sb.Append(rune.ToString());
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Filtra una stringa intera, poi normalizza gli spazi.</summary>
    public static string Filter(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            string? mapped = MapCharacter(rune.ToString());
            if (mapped is not null)
            {
                sb.Append(mapped);
            }
        }

        return CollapseSpaces(sb.ToString());
    }

    /// <summary>Riduce sequenze di spazi a uno solo e toglie gli spazi ai bordi.</summary>
    public static string CollapseSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>True se il testo contiene almeno una lettera o una cifra.</summary>
    public static bool HasLetterOrDigit(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sostituzioni di singoli caratteri del dizionario PP-OCRv5 latin. Restituisce true se il carattere è gestito qui
    /// (replacement null = da scartare).
    /// </summary>
    private static bool MapSingle(char c, out string? replacement)
    {
        int code = c;
        replacement = code switch
        {
            0xFB01 => "fi",                     // legatura fi
            0xFB02 => "fl",                     // legatura fl
            0x00A0 or 0x202F => " ",            // spazi unificatori
            0x2044 or 0x2215 => "/",            // barra di frazione, barra di divisione
            0x2212 => "-",                      // segno meno matematico
            0x2217 => "*",                      // asterisco matematico
            0x2236 => ":",                      // rapporto
            0x3001 => ",",                      // virgola ideografica
            // Lettere greche che il riconoscitore può scambiare con le latine di forma uguale.
            0x03B1 => "a",
            0x03B5 => "e",
            0x03B9 => "i",
            0x03BA => "k",
            0x03BD => "v",
            0x03BF => "o",
            0x03C1 => "p",
            0x03C4 => "t",
            0x03C5 => "u",
            0x03C7 => "x",
            0x03BC => ((char)0x00B5).ToString(), // mu -> segno micro
            _ => null
        };

        if (replacement is not null)
        {
            return true;
        }

        // Numeri romani (maiuscoli e minuscoli) come lettere latine.
        if (code is >= 0x2160 and <= 0x216B)
        {
            replacement = RomanNumerals[code - 0x2160];
            return true;
        }

        if (code is >= 0x2170 and <= 0x217B)
        {
            replacement = RomanNumerals[code - 0x2170].ToLowerInvariant();
            return true;
        }

        // Numeri cerchiati 1-10 (bianchi e neri) come cifre.
        if (code is >= 0x2460 and <= 0x2469)
        {
            replacement = (code - 0x2460 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        if (code is >= 0x2776 and <= 0x2793)
        {
            replacement = ((code - 0x2776) % 10 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        // Da scartare: trattino morbido (invisibile), diacritici spaziatori isolati (rumore).
        if (code is 0x00AD or 0x00A8 or 0x00AF or 0x00B4 or 0x00B8)
        {
            return true;
        }

        return false;
    }

    private static readonly string[] RomanNumerals = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII"];

    private static bool IsAllowed(int c) =>
        c is >= 0x20 and <= 0x7E            // latino di base
        || c is >= 0xA0 and <= 0x24F        // supplemento Latin-1, Latin Extended-A/B (à è é ì ò ù, ß, œ, ł ...)
        || c is >= 0x2010 and <= 0x2027     // trattini, virgolette tipografiche, puntini di sospensione
        || c is >= 0x2030 and <= 0x203A     // per mille, primi, ‹ › (esclusi separatori e controlli bidirezionali 2028-202F)
        || c == 0x20AC                      // euro
        || c == 0x2116                      // №
        || c == 0x2122                      // ™
        || c == 0x1E9E;                     // ẞ maiuscola
}
