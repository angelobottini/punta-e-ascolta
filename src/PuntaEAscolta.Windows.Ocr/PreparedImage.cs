using PuntaEAscolta.Core;

namespace PuntaEAscolta.Windows.Ocr;

/// <summary>
/// Immagine BGRA pronta per il motore OCR, con i parametri necessari a riportare
/// i rettangoli riconosciuti alle coordinate dell'immagine originale.
/// </summary>
/// <param name="Bgra">Pixel BGRA 32 bit, dall'alto verso il basso, stride = Width * 4. La lunghezza utile e Width * Height * 4 (il vettore puo essere piu lungo se preso in prestito da un pool).</param>
/// <param name="Width">Larghezza dell'immagine preparata.</param>
/// <param name="Height">Altezza dell'immagine preparata.</param>
/// <param name="Scale">Fattore di ingrandimento applicato alla regione sorgente.</param>
/// <param name="PadX">Margine sinistro aggiunto, in pixel dell'immagine preparata.</param>
/// <param name="PadY">Margine superiore aggiunto, in pixel dell'immagine preparata.</param>
/// <param name="OffsetX">Origine X del ritaglio nell'immagine originale (0 se non c'e ritaglio).</param>
/// <param name="OffsetY">Origine Y del ritaglio nell'immagine originale (0 se non c'e ritaglio).</param>
internal sealed record PreparedImage(
    byte[] Bgra,
    int Width,
    int Height,
    double Scale,
    int PadX,
    int PadY,
    int OffsetX,
    int OffsetY)
{
    /// <summary>Numero di byte utili nel vettore <see cref="Bgra"/>.</summary>
    public int ByteLength => Width * Height * 4;

    /// <summary>Riporta un rettangolo dall'immagine preparata alle coordinate dell'immagine originale.</summary>
    public ImageRect ToOriginal(double x, double y, double width, double height)
    {
        double ox = (x - PadX) / Scale + OffsetX;
        double oy = (y - PadY) / Scale + OffsetY;
        return new ImageRect(ox, oy, width / Scale, height / Scale);
    }
}
