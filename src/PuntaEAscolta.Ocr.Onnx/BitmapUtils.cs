using PuntaEAscolta.Core.Abstractions;
using SkiaSharp;

namespace PuntaEAscolta.Ocr.Onnx;

/// <summary>Operazioni SkiaSharp sulle immagini catturate (BGRA32 dall'alto in basso), senza System.Drawing.</summary>
internal static class BitmapUtils
{
    /// <summary>Campionamento di qualità per ingrandimenti e ritagli ruotati (lo stesso usato da RapidOcrNet per la rete).</summary>
    internal static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>Crea una SKBitmap BGRA8888 opaca copiando i pixel della cattura (stride = Width * 4).</summary>
    internal static SKBitmap FromCapturedImage(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new ArgumentException("Immagine vuota.", nameof(image));
        }

        long expected = (long)image.Width * image.Height * 4;
        if (image.Bgra is null || image.Bgra.LongLength < expected)
        {
            throw new ArgumentException("Buffer BGRA più corto di Width * Height * 4.", nameof(image));
        }

        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        try
        {
            var dst = bitmap.GetPixelSpan();
            if (bitmap.RowBytes == image.Width * 4)
            {
                image.Bgra.AsSpan(0, (int)expected).CopyTo(dst);
            }
            else
            {
                // Caso teorico: stride diverso, copia riga per riga.
                int rowLen = image.Width * 4;
                for (int y = 0; y < image.Height; y++)
                {
                    image.Bgra.AsSpan(y * rowLen, rowLen).CopyTo(dst.Slice(y * bitmap.RowBytes, rowLen));
                }
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Per immagini molto basse (ritaglio di un tooltip, una sola riga) aggiunge bande sopra e sotto con il colore medio dei
    /// bordi, così il rilevatore ha contesto verticale. Restituisce la stessa bitmap (paddingTop = 0) se non serve.
    /// </summary>
    internal static SKBitmap Letterbox(SKBitmap src, int minHeight, double maxWidthHeightRatio, out int paddingTop)
    {
        paddingTop = 0;
        int w = src.Width, h = src.Height;
        bool tooShort = h < minHeight;
        bool tooWide = maxWidthHeightRatio > 0 && w / (double)h > maxWidthHeightRatio;
        if (!tooShort && !tooWide)
        {
            return src;
        }

        int targetH = Math.Max(minHeight, Math.Max(2 * h, (int)Math.Ceiling(w / Math.Max(maxWidthHeightRatio, 1.0))));
        int pad = (targetH - h) / 2;
        if (pad <= 0)
        {
            return src;
        }

        var info = src.Info;
        info.Height = h + 2 * pad;
        var padded = new SKBitmap(info);
        using (var canvas = new SKCanvas(padded))
        {
            canvas.Clear(EdgeColor(src));
            canvas.DrawBitmap(src, 0, pad);
        }

        paddingTop = pad;
        return padded;
    }

    /// <summary>Colore medio della prima e dell'ultima riga di pixel: sfondo plausibile per le bande.</summary>
    private static SKColor EdgeColor(SKBitmap src)
    {
        long r = 0, g = 0, b = 0, n = 0;
        int w = src.Width;
        foreach (int y in new[] { 0, src.Height - 1 })
        {
            int step = Math.Max(1, w / 64);
            for (int x = 0; x < w; x += step)
            {
                var c = src.GetPixel(x, y);
                r += c.Red; g += c.Green; b += c.Blue; n++;
            }
        }

        return n == 0 ? SKColors.Gray : new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>
    /// Ritaglia il quadrilatero (ordine: alto-sinistra, alto-destra, basso-destra, basso-sinistra) raddrizzandolo.
    /// Per i riquadri allineati agli assi è un semplice sottoinsieme; per quelli inclinati si applica una trasformazione affine.
    /// Restituisce null se il ritaglio è degenere.
    /// </summary>
    internal static SKBitmap? CropQuad(SKBitmap src, SKPointI[] quad)
    {
        if (quad.Length != 4)
        {
            return null;
        }

        SKPointI p0 = quad[0], p1 = quad[1], p2 = quad[2], p3 = quad[3];
        int width = (int)Math.Round(Distance(p0, p1));
        int height = (int)Math.Round(Distance(p0, p3));
        if (width < 2 || height < 2)
        {
            return null;
        }

        bool axisAligned = Math.Abs(p0.Y - p1.Y) <= 1 && Math.Abs(p3.Y - p2.Y) <= 1
                        && Math.Abs(p0.X - p3.X) <= 1 && Math.Abs(p1.X - p2.X) <= 1;

        var info = new SKImageInfo(width, height, src.ColorType, src.AlphaType);

        if (axisAligned)
        {
            int left = Math.Min(p0.X, p3.X), top = Math.Min(p0.Y, p1.Y);
            int right = Math.Max(p1.X, p2.X), bottom = Math.Max(p3.Y, p2.Y);
            var rect = SKRectI.Intersect(new SKRectI(left, top, right, bottom), new SKRectI(0, 0, src.Width, src.Height));
            if (rect.Width < 2 || rect.Height < 2)
            {
                return null;
            }

            info.Width = rect.Width;
            info.Height = rect.Height;
            var crop = new SKBitmap(info);
            if (!src.ExtractSubset(crop, rect))
            {
                crop.Dispose();
                return null;
            }

            // ExtractSubset condivide i pixel con l'origine: se ne fa una copia indipendente.
            var copy = crop.Copy();
            crop.Dispose();
            return copy;
        }

        // Trasformazione: porta p0 nell'origine e ruota così che il lato p0->p1 diventi orizzontale.
        float angle = MathF.Atan2(p1.Y - p0.Y, p1.X - p0.X) * 180f / MathF.PI;
        var translate = SKMatrix.CreateTranslation(-p0.X, -p0.Y);
        var rotate = SKMatrix.CreateRotationDegrees(-angle);
        var matrix = SKMatrix.Concat(rotate, translate);

        var result = new SKBitmap(info);
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(SKColors.Black);
            canvas.SetMatrix(matrix);
            using var srcImage = SKImage.FromBitmap(src);
            canvas.DrawImage(srcImage, 0, 0, Sampling);
        }

        return result;
    }

    internal static double Distance(SKPointI a, SKPointI b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
