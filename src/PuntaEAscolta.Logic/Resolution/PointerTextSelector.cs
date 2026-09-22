using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Logic.Resolution;

/// <summary>Testo scelto tra le righe OCR. Text è già ripulito (scorciatoie e simboli spuri tolti).</summary>
public sealed record OcrSelection(ReadSource Source, string Text, int LineCount);

/// <summary>Sceglie, tra le righe OCR di una zona, che cosa leggere in base alla posizione del puntatore. Vedi docs/DESIGN.md sez. 3.1 e docs/research/probe-affinity.md.</summary>
public static class PointerTextSelector
{
    /// <param name="pointerX">Coordinata X del puntatore nell'immagine passata all'OCR.</param>
    /// <param name="pointerY">Coordinata Y del puntatore nell'immagine passata all'OCR.</param>
    /// <param name="wholeZone">True per leggere tutte le righe della zona, ordinate (pressione prolungata).</param>
    /// <param name="clipTo">Se presente, considera solo le righe che intersecano questo rettangolo (es. rettangolo dell'elemento trovato dall'accessibilità).</param>
    public static OcrSelection? Select(OcrResult result, double pointerX, double pointerY, OcrSettings settings, bool wholeZone, ImageRect? clipTo = null)
        => throw new NotImplementedException();
}
