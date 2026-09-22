using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Logic.Text;

/// <summary>Pulizia delle etichette dell'interfaccia e delle righe OCR prima della pronuncia. Vedi docs/DESIGN.md sez. 3.1.</summary>
public static class LabelCleaner
{
    /// <summary>Etichetta proveniente dall'accessibilità (marcatori di tasto di accesso, scorciatoie in coda, puntini, frecce).</summary>
    public static string Clean(string raw, UiElementKind kind, ReadingSettings settings) => throw new NotImplementedException();

    /// <summary>Riga proveniente dall'OCR (scorciatoia a destra, simboli spuri isolati a inizio riga).</summary>
    public static string CleanOcrLine(string line, ReadingSettings settings) => throw new NotImplementedException();

    /// <summary>True se il testo è, con tolleranza agli errori OCR, una scorciatoia da tastiera come "Ctrl+N", "CtrI + Maiusc + S", "Alt+F4", "F5".</summary>
    public static bool IsKeyboardShortcut(string text) => throw new NotImplementedException();
}
