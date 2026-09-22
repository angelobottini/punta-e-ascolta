using System.Text.RegularExpressions;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Logic.Text;

/// <summary>Stima leggera della lingua ("it" o "en") di etichette e frasi. In dubbio restituisce "it". Vedi docs/DESIGN.md sez. 3.1.</summary>
public static partial class LanguageGuesser
{
    private static readonly HashSet<string> ItalianWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "il", "lo", "la", "gli", "le", "un", "una", "uno", "di", "del", "della", "dello", "dei", "degli", "delle", "da", "dal", "dalla", "in", "nel", "nella", "nei", "nelle",
        "con", "su", "sul", "sulla", "per", "tra", "fra", "che", "chi", "non", "più", "anche", "come", "dove", "quando", "perché", "questo", "questa", "questi", "queste", "quello", "quella",
        "è", "sono", "sei", "siamo", "siete", "era", "essere", "ha", "hai", "ho", "hanno", "avere", "fa", "fare", "va", "vai", "andare", "può", "puoi", "vuoi", "deve", "devi",
        "nuovo", "nuova", "apri", "apre", "chiudi", "chiude", "salva", "salvare", "elimina", "taglia", "copia", "incolla", "annulla", "ripeti", "ripristina", "modifica", "visualizza", "inserisci",
        "formato", "strumenti", "finestra", "aiuto", "guida", "opzioni", "impostazioni", "preferenze", "livello", "livelli", "immagine", "selezione", "seleziona", "tutto", "tutti", "tutte",
        "esporta", "importa", "stampa", "anteprima", "zoom", "colore", "colori", "pennello", "testo", "tabella", "cella", "foglio", "pagina", "documento", "file", "recenti", "esci",
        "sì", "no", "ok", "conferma", "avanti", "indietro", "fine", "inizio", "cerca", "trova", "sostituisci", "riga", "colonna", "carattere", "paragrafo", "elenco", "immagini", "forme",
        "grassetto", "corsivo", "sottolineato", "allinea", "centra", "giustifica", "sinistra", "destra", "sopra", "sotto", "dentro", "fuori", "senza", "sempre", "mai", "oggi", "ieri", "domani",
        "buongiorno", "grazie", "prego", "ciao", "signore", "signora", "attenzione", "vietato", "accesso", "uscita", "entrata", "ingresso", "chiuso", "aperto", "orario", "ore",
    };

    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "to", "and", "or", "in", "on", "at", "for", "with", "from", "by", "as", "is", "are", "was", "were", "be", "been", "this", "that", "these", "those",
        "it", "its", "you", "your", "we", "our", "they", "their", "he", "she", "his", "her", "not", "no", "yes", "all", "any", "some", "more", "most", "new", "open", "close", "save",
        "file", "edit", "view", "insert", "format", "tools", "window", "help", "layer", "layers", "brush", "brushes", "export", "import", "undo", "redo", "cut", "copy", "paste", "delete",
        "select", "selection", "filter", "filters", "adjust", "adjustment", "adjustments", "image", "images", "cancel", "apply", "zoom", "settings", "options", "preferences", "print",
        "page", "pages", "document", "documents", "recent", "exit", "quit", "search", "find", "replace", "text", "table", "cell", "sheet", "row", "column", "bold", "italic", "underline",
        "align", "center", "left", "right", "top", "bottom", "width", "height", "size", "color", "colour", "opacity", "blend", "mode", "mask", "crop", "resize", "rotate", "flip",
        "canvas", "artboard", "effects", "styles", "swatches", "history", "navigator", "transform", "warp", "liquify", "sharpen", "blur", "noise", "gradient", "fill", "stroke", "pen",
        "move", "hand", "eraser", "clone", "stamp", "heal", "dodge", "burn", "sponge", "smudge", "path", "shape", "shapes", "vector", "pixel", "persona", "develop", "tone", "mapping",
        "save as", "without", "always", "never", "today", "please", "thank", "welcome", "warning", "error", "ok", "done", "next", "back", "finish", "start", "stop", "play", "pause",
    };

    [GeneratedRegex(@"[\p{L}']+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    /// <summary>Restituisce "it", "en" oppure null se il testo non contiene lettere. In dubbio restituisce "it".</summary>
    public static string? Guess(string text, LabelLanguageMode mode = LabelLanguageMode.Auto)
    {
        if (mode == LabelLanguageMode.Italian) return "it";
        if (mode == LabelLanguageMode.English) return "en";
        if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter)) return null;

        double it = 0, en = 0;
        int words = 0;
        foreach (Match m in Words().Matches(text))
        {
            var w = m.Value.Trim('\'');
            if (w.Length == 0) continue;
            words++;
            if (ItalianWords.Contains(w)) it += 2;
            if (EnglishWords.Contains(w)) en += 2;

            // Lettere e desinenze
            foreach (var c in w)
            {
                if (c is 'à' or 'è' or 'é' or 'ì' or 'ò' or 'ù' or 'À' or 'È' or 'É' or 'Ì' or 'Ò' or 'Ù') it += 1.5;
                if (c is 'k' or 'w' or 'y' or 'x' or 'j' or 'K' or 'W' or 'Y' or 'X' or 'J') en += 0.6;
            }
            var lower = w.ToLowerInvariant();
            if (lower.Length >= 4)
            {
                if (lower.EndsWith("zione") || lower.EndsWith("zioni") || lower.EndsWith("mento") || lower.EndsWith("menti") || lower.EndsWith("ità") || lower.EndsWith("are") || lower.EndsWith("ere") || lower.EndsWith("ire") || lower.EndsWith("ato") || lower.EndsWith("ata") || lower.EndsWith("ati") || lower.EndsWith("ate") || lower.EndsWith("ezza") || lower.EndsWith("aggio") || lower.EndsWith("ione")) it += 0.8;
                if (lower.EndsWith("tion") || lower.EndsWith("tions") || lower.EndsWith("ing") || lower.EndsWith("ment") || lower.EndsWith("ments") || lower.EndsWith("ness") || lower.EndsWith("ly") || lower.EndsWith("ed") || lower.EndsWith("er") || lower.EndsWith("ers") || lower.EndsWith("ity") || lower.EndsWith("ous") || lower.EndsWith("ful") || lower.EndsWith("less")) en += 0.8;
                if (lower.Contains("th") || lower.Contains("sh") || lower.Contains("ck") || lower.Contains("wh") || lower.EndsWith("gh")) en += 0.5;
                if (lower.Contains("gli") || lower.Contains("zz") || lower.Contains("cch") || lower.Contains("ggh") || lower.EndsWith("ssi")) it += 0.5;
            }
            // Parole italiane finiscono quasi sempre per vocale
            char last = lower[^1];
            if (last is 'a' or 'e' or 'i' or 'o' or 'u') it += 0.3; else en += 0.25;
        }
        if (words == 0) return null;
        return en > it + 1.0 ? "en" : "it";
    }
}
