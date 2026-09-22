using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Logic.Text;

/// <summary>Stima leggera della lingua ("it" o "en") di etichette e frasi. Vedi docs/DESIGN.md sez. 3.1.</summary>
public static class LanguageGuesser
{
    /// <summary>Restituisce "it", "en" oppure null se il testo non contiene lettere. In dubbio restituisce "it".</summary>
    public static string? Guess(string text, LabelLanguageMode mode = LabelLanguageMode.Auto) => throw new NotImplementedException();
}
