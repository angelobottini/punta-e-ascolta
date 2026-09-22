using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PuntaEAscolta.Logic.Text;

/// <summary>Rimuove emoji ed emoticon: non vanno mai pronunciate (requisito del committente). Vedi docs/DESIGN.md sez. 3.1.</summary>
public static partial class EmojiFilter
{
    /// <summary>
    /// Emoticon testuali riconosciute solo se isolate (delimitate da spazi o estremi del testo). Occhi solo ":", ";", "=":
    /// "8", "x", "X" e "D" come occhi toglievano testo vero ("Windows XP", "8) Salva il file", "Unità D: piena", "x3").
    /// Restano le forme inconfondibili "xD", "XD", "8-)", "B-)".
    /// </summary>
    [GeneratedRegex(@"(?<=^|\s)(?:[:;=][-o^']?[)(DPpOo3\]\[/\\|*]|[xX]D|8-\)|B-\)|[)(][-o^']?[:;=]|<3|</3|\^_\^|\^-\^|-_-|o_O|O_o|T_T|>_<|:-?\*|;-?\*|:'\(|:'-\()(?=$|\s|[.,!?])", RegexOptions.CultureInvariant)]
    private static partial Regex Emoticons();

    /// <summary>Spazi multipli e spazi prima della punteggiatura lasciati dalla rimozione.</summary>
    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"\s+([,.;:!?])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforePunct();

    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var sb = new StringBuilder(text.Length);
        var e = StringInfo.GetTextElementEnumerator(text);
        bool removedSomething = false;
        while (e.MoveNext())
        {
            var element = e.GetTextElement();
            if (IsEmojiElement(element)) { removedSomething = true; sb.Append(' '); continue; }
            sb.Append(element);
        }

        var result = sb.ToString();
        var withoutEmoticons = Emoticons().Replace(result, " ");
        if (withoutEmoticons != result) { removedSomething = true; result = withoutEmoticons; }

        if (!removedSomething) return text;
        result = MultiSpace().Replace(result, " ");
        result = SpaceBeforePunct().Replace(result, "$1");
        return result.Trim();
    }

    /// <summary>Un elemento di testo (grapheme cluster) è emoji se il suo primo scalare è pittografico, oppure se è composto da tastierino, bandiera, ZWJ o selettore emoji.</summary>
    private static bool IsEmojiElement(string element)
    {
        if (element.Length == 0) return false;
        var first = Rune.GetRuneAt(element, 0);
        int v = first.Value;

        if (IsPictographic(v)) return true;

        // Tastierino: cifra/#/* + U+FE0F + U+20E3
        if (element.Contains((char)0x20E3)) return true;

        // Un carattere di testo normale seguito dal selettore di variante emoji (es. ☺\uFE0F, ❤\uFE0F, ©\uFE0F non lo consideriamo emoji se lettera/cifra)
        if (element.Length > 1 && element.Contains((char)0xFE0F) && !char.IsLetterOrDigit(element[0])) return true;

        // Solo modificatori o ZWJ orfani
        return v is >= 0x1F3FB and <= 0x1F3FF or 0x200D;
    }

    private static bool IsPictographic(int v) =>
        v is >= 0x1F000 and <= 0x1FAFF   // Mahjong, domino, carte, emoticon, trasporti, simboli supplementari, esteso-A
        or >= 0x1FB00 and <= 0x1FBFF     // simboli per computer legacy
        or >= 0x2600 and <= 0x26FF       // simboli vari (☀ ☎ ⚠ ...)
        or >= 0x2700 and <= 0x27BF       // dingbat (✂ ✅ ✈ ❌ ...)
        or >= 0x2B00 and <= 0x2BFF       // frecce e simboli vari (⬆ ⭐ ...)
        or >= 0x1F1E6 and <= 0x1F1FF     // indicatori regionali (bandiere)
        or >= 0x1F3FB and <= 0x1F3FF     // toni di pelle
        or >= 0xE0020 and <= 0xE007F     // tag (bandiere di sotto-regioni)
        or 0x231A or 0x231B or 0x2328 or 0x23CF or >= 0x23E9 and <= 0x23F3 or >= 0x23F8 and <= 0x23FA
        or 0x24C2 or 0x25AA or 0x25AB or 0x25B6 or 0x25C0 or >= 0x25FB and <= 0x25FE
        or 0x2934 or 0x2935 or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x203C or 0x2049 or 0x2139 or >= 0x2194 and <= 0x2199 or 0x21A9 or 0x21AA;
}
