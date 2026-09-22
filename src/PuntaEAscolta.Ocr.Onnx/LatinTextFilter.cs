using System.Text;

namespace PuntaEAscolta.Ocr.Onnx;

/// <summary>
/// Filtro dei caratteri per la sintesi vocale: tiene solo lettere latine, cifre, punteggiatura e pochi simboli
/// comuni (€, °, %, ™). Scarta greco, cirillico, frecce, forme geometriche, operatori matematici, numeri cerchiati,
/// punteggiatura CJK e il carattere di sostituzione, che il dizionario PP-OCRv5 latin può emettere sul rumore.
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

        // Legature e sostituzioni esplicite prima del filtro per intervalli.
        switch (ch)
        {
            case "ﬁ": return "fi";
            case "ﬂ": return "fl";
            case "\u00A0": return " ";   // spazio unificatore -> spazio
            case "­": return null;  // trattino morbido: invisibile
            case "⁄": return "/";   // barra di frazione
            case "−": return "-";   // segno meno matematico
            case "∕": return "/";   // barra di divisione
            case "∗": return "*";   // asterisco matematico
            case "∶": return ":";   // rapporto
            case "¨":               // diacritici spaziatori isolati: rumore
            case "¯":
            case "´":
            case "¸":
                return null;
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

    private static bool IsAllowed(int c) =>
        c is >= 0x20 and <= 0x7E            // latino di base
        || c is >= 0xA0 and <= 0x24F        // supplemento Latin-1, Latin Extended-A/B (à è é ì ò ù, ß, œ, ł ...)
        || c is >= 0x2010 and <= 0x203A     // trattini, virgolette tipografiche, puntini di sospensione, per mille, ‹ ›
        || c == 0x20AC                      // euro
        || c == 0x2116                      // №
        || c == 0x2122                      // ™
        || c == 0x1E9E;                     // ẞ maiuscola
}
