using System.Globalization;
using System.Text;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Speech.Dictation;

/// <summary>Comando vocale riconosciuto in un enunciato intero. None = testo normale da inserire.</summary>
public enum VoiceCommand
{
    None,

    /// <summary>Cancella l'ultimo testo inserito (Backspace per ogni carattere).</summary>
    Delete,

    /// <summary>Invio: nuova riga o nuovo paragrafo.</summary>
    NewLine,

    /// <summary>Rilegge a voce l'ultimo testo inserito.</summary>
    ReadAgain
}

/// <summary>
/// Riconoscimento dei comandi vocali a frase intera ("cancella", "a capo", "rileggi" e gli alias scelti dall'assistente).
/// Il confronto è sull'intero enunciato, senza distinzione di maiuscole, punteggiatura, accenti e spazi multipli:
/// "Cancella." e "cancella" sono uguali, "cancella la riga" non è un comando. Vedi docs/research/dictation.md sez. 2.6.
/// </summary>
public static class VoiceCommands
{
    /// <summary>Restituisce il comando che corrisponde all'intero enunciato, oppure None.</summary>
    public static VoiceCommand Match(string? utterance, DictationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string key = Normalize(utterance);
        if (key.Length == 0) return VoiceCommand.None;
        if (MatchesAny(key, settings.CommandDelete)) return VoiceCommand.Delete;
        if (MatchesAny(key, settings.CommandNewLine)) return VoiceCommand.NewLine;
        if (MatchesAny(key, settings.CommandReadAgain)) return VoiceCommand.ReadAgain;
        return VoiceCommand.None;
    }

    public static bool TryMatch(string? utterance, DictationSettings settings, out VoiceCommand command)
    {
        command = Match(utterance, settings);
        return command != VoiceCommand.None;
    }

    /// <summary>
    /// Forma canonica di un enunciato: minuscole, senza accenti, solo lettere e cifre separate da un singolo spazio.
    /// Si applica sia al testo riconosciuto sia alle voci configurate, così l'assistente può scriverle come vuole.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        foreach (char c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue; // accenti e segni diacritici
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true; // spazi, punteggiatura, apostrofi, trattini: tutti separatori
            }
        }
        return builder.ToString();
    }

    private static bool MatchesAny(string normalizedUtterance, IEnumerable<string>? phrases)
    {
        if (phrases is null) return false;
        foreach (var phrase in phrases)
        {
            string normalized = Normalize(phrase);
            if (normalized.Length > 0 && normalized == normalizedUtterance) return true;
        }
        return false;
    }
}
