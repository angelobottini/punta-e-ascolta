namespace PuntaEAscolta.Core.Abstractions;

/// <summary>Immagine catturata dallo schermo: BGRA 32 bit, dall'alto verso il basso, senza padding di riga (stride = Width * 4).</summary>
/// <param name="ScreenBounds">Rettangolo di schermo effettivamente catturato (già ritagliato sul monitor).</param>
/// <param name="DpiScale">Fattore di scala del monitor (1.0 = 100%, 1.5 = 150%).</param>
public sealed record CapturedImage(byte[] Bgra, int Width, int Height, ScreenRect ScreenBounds, double DpiScale);

public interface IScreenCapture
{
    /// <summary>Cattura il rettangolo richiesto, ritagliato sul monitor che contiene anchor. Non include il cursore.</summary>
    CapturedImage Capture(ScreenRect desired, ScreenPoint anchor);

    double GetDpiScale(ScreenPoint point);
}

public sealed record OcrWord(string Text, ImageRect Box);

/// <summary>Riga riconosciuta. Box e parole sono in coordinate dell'immagine ORIGINALE passata a RecognizeAsync (non di quella ingrandita internamente).</summary>
public sealed record OcrLine(string Text, ImageRect Box, IReadOnlyList<OcrWord> Words, double? Confidence = null);

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, string EngineName, int ElapsedMs)
{
    public static OcrResult Empty(string engine) => new(Array.Empty<OcrLine>(), engine, 0);
}

/// <summary>Motore OCR. Le implementazioni curano da sé il pre-trattamento (ingrandimento, contrasto) e riportano le coordinate all'immagine originale.</summary>
public interface IOcrEngine : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    Task<OcrResult> RecognizeAsync(CapturedImage image, CancellationToken ct);
}

/// <summary>
/// Capacità facoltativa di un motore OCR: riconoscimento mirato attorno a un punto.
/// x e y sono in pixel dell'immagine passata (stesso sistema delle coordinate restituite).
/// </summary>
public interface IPointOcrEngine
{
    /// <summary>
    /// Passaggio mirato attorno al punto (Windows: ritaglio circa 400x120, ingrandimento 2x, scala di grigi e stiramento
    /// del contrasto, che recupera il testo quasi invisibile come le voci disabilitate). Da usare quando il passaggio
    /// normale non trova nulla sulla riga del puntatore.
    /// </summary>
    Task<OcrResult> RecognizeAroundPointAsync(CapturedImage image, double x, double y, CancellationToken ct);
}
