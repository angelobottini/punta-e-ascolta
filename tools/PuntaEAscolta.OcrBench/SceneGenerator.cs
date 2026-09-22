using System.Text;
using SkiaSharp;

namespace PuntaEAscolta.OcrBench;

/// <summary>
/// Crea immagini sintetiche di "testo di scena" (cartelli) con il relativo file di righe attese:
/// cartello giallo grande, cartello rosso ruotato di 8 gradi, cartello piccolo rumoroso a bassa risoluzione.
/// </summary>
internal static class SceneGenerator
{
    private static readonly string EGraveUpper = ((char)0x00C8).ToString(); // E con accento grave maiuscola
    private static readonly string IGrave = ((char)0x00EC).ToString();      // i con accento grave

    public static void Generate(string directory)
    {
        Directory.CreateDirectory(directory);
        YellowSign(directory);
        RotatedRedSign(directory);
        SmallNoisySign(directory);
    }

    private static void YellowSign(string dir)
    {
        const int w = 1000, h = 600;
        using var bmp = NewBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            PaintWall(canvas, w, h, new SKColor(120, 110, 100), new SKColor(70, 65, 60), seed: 1);

            var sign = new SKRect(150, 140, 850, 460);
            using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(sign.Left + 10, sign.Top + 12, sign.Right + 10, sign.Bottom + 12), 18, 18, shadow);
            using var yellow = new SKPaint { Color = new SKColor(250, 204, 21), IsAntialias = true };
            canvas.DrawRoundRect(sign, 18, 18, yellow);
            using var border = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10 };
            canvas.DrawRoundRect(new SKRect(sign.Left + 16, sign.Top + 16, sign.Right - 16, sign.Bottom - 16), 10, 10, border);

            using var ink = new SKPaint { Color = new SKColor(20, 20, 20), IsAntialias = true };
            using var big = new SKFont(Bold(), 84);
            using var mid = new SKFont(Bold(), 58);
            canvas.DrawText("ATTENZIONE:", w / 2f, 270, SKTextAlign.Center, big, ink);
            canvas.DrawText(EGraveUpper + " VIETATO L'ACCESSO", w / 2f, 370, SKTextAlign.Center, mid, ink);
        }

        Save(bmp, Path.Combine(dir, "scena-cartello-giallo.png"));
        WriteExpected(Path.Combine(dir, "scena-cartello-giallo.txt"), "ATTENZIONE:", EGraveUpper + " VIETATO L'ACCESSO");
        Console.WriteLine("scena-cartello-giallo.png 1000x600: punto suggerito 500 350");
    }

    private static void RotatedRedSign(string dir)
    {
        const int w = 1000, h = 600;
        using var bmp = NewBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            PaintWall(canvas, w, h, new SKColor(170, 185, 190), new SKColor(110, 125, 130), seed: 2);

            canvas.Save();
            canvas.RotateDegrees(8, w / 2f, h / 2f);
            var sign = new SKRect(190, 170, 810, 430);
            using var red = new SKPaint { Color = new SKColor(200, 30, 35), IsAntialias = true };
            canvas.DrawRoundRect(sign, 14, 14, red);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var big = new SKFont(Bold(), 62);
            using var small = new SKFont(Regular(), 40);
            canvas.DrawText("VIETATO FUMARE", w / 2f, 290, SKTextAlign.Center, big, white);
            canvas.DrawText("Area riservata al personale", w / 2f, 370, SKTextAlign.Center, small, white);
            canvas.Restore();
        }

        Save(bmp, Path.Combine(dir, "scena-rosso-ruotato.png"));
        WriteExpected(Path.Combine(dir, "scena-rosso-ruotato.txt"), "VIETATO FUMARE", "Area riservata al personale");
        Console.WriteLine("scena-rosso-ruotato.png 1000x600: punto suggerito 500 265");
    }

    private static void SmallNoisySign(string dir)
    {
        // Disegnato a 2x e poi ridotto senza filtro, con rumore e compressione JPEG: come una foto piccola in una pagina web.
        const int w = 360, h = 220;
        using var hi = NewBitmap(w * 2, h * 2);
        using (var canvas = new SKCanvas(hi))
        {
            PaintWall(canvas, w * 2, h * 2, new SKColor(95, 120, 80), new SKColor(60, 80, 50), seed: 3);
            var sign = new SKRect(80, 120, 640, 330);
            using var blue = new SKPaint { Color = new SKColor(20, 70, 160), IsAntialias = true };
            canvas.DrawRect(sign, blue);
            using var white = new SKPaint { Color = new SKColor(245, 245, 245), IsAntialias = true };
            using var big = new SKFont(Bold(), 40);
            using var small = new SKFont(Regular(), 28);
            canvas.DrawText("CHIUSO PER FERIE", 360, 205, SKTextAlign.Center, big, white);
            canvas.DrawText("Riapre luned" + IGrave + " 5 ottobre", 360, 270, SKTextAlign.Center, small, white);
        }

        using var low = hi.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Nearest));
        AddNoise(low, amplitude: 22, seed: 4);

        using var jpegImage = SKImage.FromBitmap(low);
        using var jpeg = jpegImage.Encode(SKEncodedImageFormat.Jpeg, 35);
        using var decoded = SKBitmap.Decode(jpeg);
        Save(decoded, Path.Combine(dir, "scena-piccolo-rumoroso.png"));
        WriteExpected(Path.Combine(dir, "scena-piccolo-rumoroso.txt"), "CHIUSO PER FERIE", "Riapre luned" + IGrave + " 5 ottobre");
        Console.WriteLine("scena-piccolo-rumoroso.png 360x220: punto suggerito 180 100");
    }

    private static SKBitmap NewBitmap(int w, int h) => new(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));

    private static SKTypeface Bold() => SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default;

    private static SKTypeface Regular() => SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal) ?? SKTypeface.Default;

    /// <summary>Sfondo a gradiente con qualche macchia, per non avere un fondo perfettamente uniforme.</summary>
    private static void PaintWall(SKCanvas canvas, int w, int h, SKColor top, SKColor bottom, int seed)
    {
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w * 0.3f, h), [top, bottom], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, w, h, paint);

        var rnd = new Random(seed);
        using var spot = new SKPaint { IsAntialias = true };
        for (int i = 0; i < 60; i++)
        {
            byte a = (byte)rnd.Next(10, 40);
            spot.Color = rnd.Next(2) == 0 ? new SKColor(255, 255, 255, a) : new SKColor(0, 0, 0, a);
            canvas.DrawCircle(rnd.Next(w), rnd.Next(h), rnd.Next(4, 40), spot);
        }
    }

    private static void AddNoise(SKBitmap bmp, int amplitude, int seed)
    {
        var rnd = new Random(seed);
        var px = bmp.GetPixelSpan();
        for (int y = 0; y < bmp.Height; y++)
        {
            int row = y * bmp.RowBytes;
            for (int x = 0; x < bmp.Width; x++)
            {
                int i = row + x * 4;
                int n = rnd.Next(-amplitude, amplitude + 1);
                for (int c = 0; c < 3; c++)
                {
                    px[i + c] = (byte)Math.Clamp(px[i + c] + n, 0, 255);
                }
            }
        }
    }

    private static void Save(SKBitmap bmp, string path)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void WriteExpected(string path, params string[] lines) =>
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
}
