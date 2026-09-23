using System.Text;

namespace PuntaEAscolta.Speech.ElevenLabs;

/// <summary>
/// Pulizia della chiave API incollata dall'assistente (finestra impostazioni, --set-key). Copiando dal sito, da una mail o da
/// un documento arrivano spesso caratteri invisibili (spazi a larghezza zero, BOM, spazi unificatori), virgolette attorno o
/// l'etichetta "xi-api-key:": ElevenLabs rifiuterebbe la chiave anche se è giusta. Dopo la pulizia si accettano solo lettere,
/// cifre, trattino basso e trattino (le chiavi ElevenLabs sono "sk_" seguito da cifre esadecimali). La chiave non compare mai
/// nei messaggi d'errore.
/// </summary>
public static class ElevenLabsKey
{
    public const int MaxLength = 256;

    private const string Label = "xi-api-key";

    /// <summary>Caratteri invisibili o spazi "speciali" tolti ovunque si trovino.</summary>
    private static readonly char[] s_invisible =
    {
        (char)0x200B, // spazio a larghezza zero
        (char)0x200C, // non-unione a larghezza zero
        (char)0x200D, // unione a larghezza zero
        (char)0x200E, // segno da sinistra a destra
        (char)0x200F, // segno da destra a sinistra
        (char)0x2060, // word joiner
        (char)0xFEFF, // BOM
        (char)0x00AD, // trattino morbido
        (char)0x00A0, // spazio unificatore
        (char)0x202F, // spazio unificatore stretto
        (char)0x2007, // spazio per cifre
    };

    /// <summary>Virgolette che si tolgono dai due estremi (dritte, tipografiche, apice inverso, caporali).</summary>
    private static readonly char[] s_quotes =
    {
        '"', '\'', '`', (char)0x201C, (char)0x201D, (char)0x201E, (char)0x2018, (char)0x2019, (char)0x00AB, (char)0x00BB,
    };

    /// <summary>
    /// Pulisce la chiave: toglie caratteri invisibili e spazi unificatori, spazi agli estremi, virgolette attorno ed
    /// eventuale etichetta iniziale "xi-api-key:" (o "xi-api-key ="). Poi controlla che restino solo [A-Za-z0-9_-].
    /// Restituisce false con un messaggio in italiano (senza la chiave) se non è utilizzabile.
    /// </summary>
    public static bool TrySanitize(string? raw, out string key, out string? error)
    {
        key = "";
        error = null;
        if (raw is null)
        {
            error = "Nessuna chiave: incollare la chiave API di ElevenLabs.";
            return false;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (Array.IndexOf(s_invisible, c) < 0) sb.Append(c);
        }
        string text = sb.ToString();

        // Più giri: l'etichetta può stare dentro le virgolette e la chiave dopo l'etichetta può averne altre.
        for (int round = 0; round < 3; round++)
        {
            string before = text;
            text = text.Trim().Trim(s_quotes).Trim();
            if (text.StartsWith(Label, StringComparison.OrdinalIgnoreCase))
            {
                // Anche nella forma JSON: "xi-api-key": "sk_..." (la virgoletta di chiusura dell'etichetta resta qui).
                string rest = text[Label.Length..].TrimStart().TrimStart(s_quotes).TrimStart();
                if (rest.StartsWith(':') || rest.StartsWith('=')) text = rest[1..];
            }
            if (text == before) break;
        }
        text = text.Trim();

        if (text.Length == 0)
        {
            error = "La chiave è vuota: incollare la chiave API di ElevenLabs.";
            return false;
        }
        if (text.Length > MaxLength)
        {
            error = $"La chiave è troppo lunga (più di {MaxLength} caratteri): copiare solo la chiave.";
            return false;
        }
        foreach (char c in text)
        {
            if (!IsAllowed(c))
            {
                error = char.IsWhiteSpace(c)
                    ? "La chiave contiene spazi in mezzo: copiarla di nuovo per intero dal sito di ElevenLabs."
                    : "La chiave contiene caratteri non ammessi: una chiave ElevenLabs ha solo lettere, numeri, trattino basso e trattino " +
                      "(di solito inizia con sk_). Copiarla di nuovo dal sito di ElevenLabs.";
                return false;
            }
        }

        key = text;
        return true;
    }

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
}
