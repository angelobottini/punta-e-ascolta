using PuntaEAscolta.Core.Abstractions;
using SkiaSharp;

namespace PuntaEAscolta.Ocr.Onnx;

/// <summary>
/// Sistema di riferimento di un ritaglio: il pixel (u, v) del ritaglio corrisponde al punto
/// Origin + u * AxisU + v * AxisV dell'immagine da cui è stato preso.
/// </summary>
internal readonly record struct CropFrame(SKPoint Origin, SKPoint AxisU, SKPoint AxisV)
{
    public SKPoint Map(double u, double v) =>
        new((float)(Origin.X + u * AxisU.X + v * AxisV.X), (float)(Origin.Y + u * AxisU.Y + v * AxisV.Y));
}

/// <summary>Bordi (in pixel del ritaglio, estremi esclusi a destra e in basso) della zona che contiene il testo.</summary>
internal readonly record struct InkBox(int Left, int Top, int Right, int Bottom);

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

    /// <summary>Copia indipendente del rettangolo richiesto (già interno all'immagine).</summary>
    internal static SKBitmap CopyRegion(SKBitmap src, SKRectI region)
    {
        var info = new SKImageInfo(region.Width, region.Height, src.ColorType, src.AlphaType);
        var copy = new SKBitmap(info);
        try
        {
            var from = src.GetPixelSpan();
            var to = copy.GetPixelSpan();
            int bpp = src.BytesPerPixel;
            int rowLen = region.Width * bpp;
            for (int y = 0; y < region.Height; y++)
            {
                from.Slice((region.Top + y) * src.RowBytes + region.Left * bpp, rowLen).CopyTo(to.Slice(y * copy.RowBytes, rowLen));
            }

            return copy;
        }
        catch
        {
            copy.Dispose();
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
    /// Per i riquadri allineati agli assi è un semplice sottoinsieme; per quelli inclinati si applica una rotazione.
    /// Restituisce null se il ritaglio è degenere; frame permette di riportare i pixel del ritaglio sull'immagine.
    /// </summary>
    internal static SKBitmap? CropQuad(SKBitmap src, SKPointI[] quad, out CropFrame frame)
    {
        frame = default;
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

        if (axisAligned)
        {
            int left = Math.Min(p0.X, p3.X), top = Math.Min(p0.Y, p1.Y);
            int right = Math.Max(p1.X, p2.X), bottom = Math.Max(p3.Y, p2.Y);
            var rect = SKRectI.Intersect(new SKRectI(left, top, right, bottom), new SKRectI(0, 0, src.Width, src.Height));
            if (rect.Width < 2 || rect.Height < 2)
            {
                return null;
            }

            frame = new CropFrame(new SKPoint(rect.Left, rect.Top), new SKPoint(1, 0), new SKPoint(0, 1));
            return CopyRegion(src, rect);
        }

        // Rotazione: porta p0 nell'origine e ruota così che il lato p0->p1 diventi orizzontale.
        float angleRad = MathF.Atan2(p1.Y - p0.Y, p1.X - p0.X);
        var translate = SKMatrix.CreateTranslation(-p0.X, -p0.Y);
        var rotate = SKMatrix.CreateRotation(-angleRad);
        var matrix = SKMatrix.Concat(rotate, translate);

        var result = new SKBitmap(new SKImageInfo(width, height, src.ColorType, src.AlphaType));
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(SKColors.Black);
            canvas.SetMatrix(matrix);
            using var srcImage = SKImage.FromBitmap(src);
            canvas.DrawImage(srcImage, 0, 0, Sampling);
        }

        float cos = MathF.Cos(angleRad), sin = MathF.Sin(angleRad);
        frame = new CropFrame(new SKPoint(p0.X, p0.Y), new SKPoint(cos, sin), new SKPoint(-sin, cos));
        return result;
    }

    /// <summary>Restituisce una nuova bitmap larga factor volte l'originale, stessa altezza.</summary>
    internal static SKBitmap StretchHorizontally(SKBitmap src, double factor)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * factor));
        return src.Resize(new SKImageInfo(w, src.Height, src.ColorType, src.AlphaType), Sampling);
    }

    /// <summary>
    /// Trova i bordi del testo dentro il ritaglio di una riga (il riquadro del rilevatore è più largo del testo):
    /// sfondo = mediana della luminanza sul bordo del ritaglio, "inchiostro" = pixel che se ne discostano. Delle fasce
    /// orizzontali di inchiostro si tiene la principale più quelle vicine (accenti, puntini); si scartano le code delle
    /// righe adiacenti che toccano il bordo. Null se il contrasto è insufficiente.
    /// </summary>
    internal static InkBox? FindInk(SKBitmap crop)
    {
        int w = crop.Width, h = crop.Height;
        if (w < 4 || h < 4 || crop.BytesPerPixel != 4)
        {
            return null;
        }

        ReadOnlySpan<byte> px = crop.GetPixelSpan();
        int rowBytes = crop.RowBytes;
        var lum = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                // Ordine BGRA: B = i, G = i + 1, R = i + 2.
                lum[y * w + x] = (byte)((px[i + 2] * 77 + px[i + 1] * 150 + px[i] * 29) >> 8);
            }
        }

        // Sfondo: mediana dei pixel di bordo.
        var border = new List<byte>(2 * (w + h));
        for (int x = 0; x < w; x++)
        {
            border.Add(lum[x]);
            border.Add(lum[(h - 1) * w + x]);
        }

        for (int y = 0; y < h; y++)
        {
            border.Add(lum[y * w]);
            border.Add(lum[y * w + w - 1]);
        }

        border.Sort();
        int bg = border[border.Count / 2];

        int maxDiff = 0;
        foreach (byte v in lum)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(v - bg));
        }

        if (maxDiff < 16)
        {
            return null;
        }

        int threshold = Math.Max(10, (int)(maxDiff * 0.4));
        int minPerRow = Math.Max(1, w / 200);
        var rowInk = new int[h];
        for (int y = 0; y < h; y++)
        {
            int count = 0;
            for (int x = 0; x < w; x++)
            {
                if (Math.Abs(lum[y * w + x] - bg) > threshold)
                {
                    count++;
                }
            }

            rowInk[y] = count >= minPerRow ? count : 0;
        }

        // Fasce di righe con inchiostro.
        var runs = new List<(int Start, int End, long Sum)>();
        for (int y = 0; y < h;)
        {
            if (rowInk[y] == 0)
            {
                y++;
                continue;
            }

            int start = y;
            long sum = 0;
            while (y < h && rowInk[y] > 0)
            {
                sum += rowInk[y];
                y++;
            }

            runs.Add((start, y, sum));
        }

        if (runs.Count == 0)
        {
            return null;
        }

        int main = 0;
        for (int i = 1; i < runs.Count; i++)
        {
            if (runs[i].Sum > runs[main].Sum)
            {
                main = i;
            }
        }

        int top = runs[main].Start, bottom = runs[main].End;
        int mainHeight = bottom - top;
        int maxGap = Math.Max(2, (int)Math.Round(mainHeight * 0.2));

        bool Acceptable((int Start, int End, long Sum) r)
        {
            bool touchesBorder = r.Start == 0 || r.End == h;
            return !(touchesBorder && r.End - r.Start < mainHeight * 0.4);
        }

        for (int i = main - 1; i >= 0 && top - runs[i].End <= maxGap && Acceptable(runs[i]); i--)
        {
            top = runs[i].Start;
        }

        for (int i = main + 1; i < runs.Count && runs[i].Start - bottom <= maxGap && Acceptable(runs[i]); i++)
        {
            bottom = runs[i].End;
        }

        // Colonne con inchiostro dentro la fascia scelta.
        int left = -1, right = -1;
        for (int x = 0; x < w; x++)
        {
            for (int y = top; y < bottom; y++)
            {
                if (Math.Abs(lum[y * w + x] - bg) > threshold)
                {
                    if (left < 0)
                    {
                        left = x;
                    }

                    right = x + 1;
                    break;
                }
            }
        }

        if (left < 0)
        {
            return null;
        }

        // Un pixel di margine, come i riquadri di Windows.Media.Ocr.
        return new InkBox(Math.Max(0, left - 1), Math.Max(0, top - 1), Math.Min(w, right + 1), Math.Min(h, bottom + 1));
    }

    internal static double Distance(SKPointI a, SKPointI b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
