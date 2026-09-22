using System.Buffers;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Ocr;

/// <summary>
/// Pre-trattamento GDI+ misurato in docs/research/ocr-windows.md e docs/research/probe-affinity.md.
/// Primo passaggio: ingrandimento bicubico adattivo alla scala del monitor, margine del colore di sfondo,
/// nessuna inversione, nessuna gamma, nessun livello automatico.
/// Secondo passaggio (basso contrasto): ritaglio intorno al puntatore, 2x, scala di grigi e stiramento
/// del contrasto fra il 1o e il 99,5o percentile della luminanza.
/// </summary>
internal static class OcrPreprocessor
{
    /// <summary>Margine aggiunto su ogni lato, in pixel dell'immagine preparata.</summary>
    public const int Padding = 24;

    /// <summary>Dimensioni minime dell'immagine data al motore: ritagli piu piccoli restituiscono testo vuoto.</summary>
    public const int MinWidth = 150;
    public const int MinHeight = 100;

    /// <summary>Ritaglio del secondo passaggio, in pixel dell'immagine originale.</summary>
    public const int LowContrastCropWidth = 400;
    public const int LowContrastCropHeight = 120;
    public const double LowContrastScale = 2.0;

    private const double LowPercentile = 0.01;
    private const double HighPercentile = 0.995;

    /// <summary>Sotto questa escursione di luminanza la zona e piatta (nessun testo): non si stira, per non gonfiare il rumore.</summary>
    private const int MinStretchRange = 8;

    /// <summary>Fattore del primo passaggio: clamp(2.0 / scala DPI, 1.0, 3.0) arrotondato a 0,25 (1,5x al 125%).</summary>
    public static double ComputeStandardScale(double dpiScale)
    {
        if (double.IsNaN(dpiScale) || double.IsInfinity(dpiScale) || dpiScale <= 0)
        {
            dpiScale = 1.0;
        }

        double raw = Math.Clamp(2.0 / dpiScale, 1.0, 3.0);
        return Math.Round(raw / 0.25, MidpointRounding.AwayFromZero) * 0.25;
    }

    /// <summary>Primo passaggio: immagine intera, colore conservato.</summary>
    public static PreparedImage PrepareStandard(CapturedImage image, uint maxDimension, ArrayPool<byte> pool)
    {
        double scale = ComputeStandardScale(image.DpiScale);
        var region = new Rectangle(0, 0, image.Width, image.Height);
        return Render(image, region, scale, grayscaleStretch: false, maxDimension, pool);
    }

    /// <summary>Secondo passaggio: ritaglio di circa 400x120 intorno a (x, y) in coordinate dell'immagine originale.</summary>
    public static PreparedImage PrepareLowContrast(CapturedImage image, double x, double y, uint maxDimension, ArrayPool<byte> pool)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) x = image.Width / 2.0;
        if (double.IsNaN(y) || double.IsInfinity(y)) y = image.Height / 2.0;

        int cropWidth = Math.Min(LowContrastCropWidth, image.Width);
        int cropHeight = Math.Min(LowContrastCropHeight, image.Height);
        int cropX = (int)Math.Round(x) - cropWidth / 2;
        int cropY = (int)Math.Round(y) - cropHeight / 2;
        cropX = Math.Clamp(cropX, 0, image.Width - cropWidth);
        cropY = Math.Clamp(cropY, 0, image.Height - cropHeight);

        var region = new Rectangle(cropX, cropY, cropWidth, cropHeight);
        return Render(image, region, LowContrastScale, grayscaleStretch: true, maxDimension, pool);
    }

    private static PreparedImage Render(CapturedImage image, Rectangle source, double scale, bool grayscaleStretch, uint maxDimension, ArrayPool<byte> pool)
    {
        // Rispetto di OcrEngine.MaxImageDimension: si riduce il fattore, mai si supera il limite.
        int maxDim = maxDimension == 0 ? int.MaxValue : (int)Math.Min(maxDimension, int.MaxValue);
        int longest = Math.Max(source.Width, source.Height);
        double maxScale = (double)Math.Max(maxDim - 2 * Padding, 1) / longest;
        if (scale > maxScale)
        {
            scale = maxScale;
        }

        int scaledWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        int scaledHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        // Margine: almeno Padding per lato, di piu se serve a raggiungere la dimensione minima.
        int padX = Math.Max(Padding, (MinWidth - scaledWidth + 1) / 2);
        int padY = Math.Max(Padding, (MinHeight - scaledHeight + 1) / 2);
        if (scaledWidth + 2 * padX > maxDim) padX = Math.Max(0, (maxDim - scaledWidth) / 2);
        if (scaledHeight + 2 * padY > maxDim) padY = Math.Max(0, (maxDim - scaledHeight) / 2);
        int destWidth = scaledWidth + 2 * padX;
        int destHeight = scaledHeight + 2 * padY;

        (float gain, float offset) = grayscaleStretch ? ComputeStretch(image, source) : (1f, 0f);
        Color border = DominantBorderColor(image, source);
        Color padColor = grayscaleStretch ? ToStretchedGray(border, gain, offset) : border;

        GCHandle handle = GCHandle.Alloc(image.Bgra, GCHandleType.Pinned);
        try
        {
            // Format32bppRgb: l'alfa delle catture (spesso 0) viene ignorato, altrimenti GDI+ disegnerebbe tutto trasparente.
            using var sourceBitmap = new Bitmap(image.Width, image.Height, image.Width * 4, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            using var destination = new Bitmap(destWidth, destHeight, PixelFormat.Format32bppArgb);
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            if (grayscaleStretch)
            {
                attributes.SetColorMatrix(GrayscaleMatrix(gain, offset));
            }

            using (var graphics = Graphics.FromImage(destination))
            {
                graphics.Clear(Color.FromArgb(255, padColor.R, padColor.G, padColor.B));
                // CompositingQuality.HighQuality NON va impostato: misurato, quadruplica il tempo (60 ms contro 16 ms su 900x300 -> 1.5x).
                graphics.InterpolationMode = scale == 1.0 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(
                    sourceBitmap,
                    new Rectangle(padX, padY, scaledWidth, scaledHeight),
                    source.X, source.Y, source.Width, source.Height,
                    GraphicsUnit.Pixel,
                    attributes);
            }

            return CopyOut(destination, scale, padX, padY, source.X, source.Y, pool);
        }
        finally
        {
            handle.Free();
        }
    }

    private static PreparedImage CopyOut(Bitmap bitmap, double scale, int padX, int padY, int offsetX, int offsetY, ArrayPool<byte> pool)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bitmap.Width * 4;
            int length = rowBytes * bitmap.Height;
            byte[] buffer = pool.Rent(length);
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, buffer, 0, length);
            }
            else
            {
                for (int row = 0; row < bitmap.Height; row++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), buffer, row * rowBytes, rowBytes);
                }
            }

            return new PreparedImage(buffer, bitmap.Width, bitmap.Height, scale, padX, padY, offsetX, offsetY);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Luminanza intera 0..255 da un pixel BGRA (pesi 29/150/77 su 256, come nella sonda).</summary>
    private static int Luminance(byte b, byte g, byte r) => (b * 29 + g * 150 + r * 77) >> 8;

    /// <summary>
    /// Stiramento fra il 1o e il 99,5o percentile della luminanza della regione. Restituisce guadagno e
    /// scostamento in unita 0..1 per la ColorMatrix (v' = v * gain + offset).
    /// </summary>
    private static (float Gain, float Offset) ComputeStretch(CapturedImage image, Rectangle region)
    {
        Span<int> histogram = stackalloc int[256];
        byte[] pixels = image.Bgra;
        int stride = image.Width * 4;
        for (int y = region.Top; y < region.Bottom; y++)
        {
            int index = y * stride + region.Left * 4;
            for (int x = 0; x < region.Width; x++, index += 4)
            {
                histogram[Luminance(pixels[index], pixels[index + 1], pixels[index + 2])]++;
            }
        }

        long total = (long)region.Width * region.Height;
        if (total <= 0)
        {
            return (1f, 0f);
        }

        long lowTarget = (long)Math.Ceiling(total * LowPercentile);
        long highTarget = (long)Math.Ceiling(total * (1.0 - HighPercentile));

        int low = 0;
        long accumulated = 0;
        for (int i = 0; i < 256; i++)
        {
            accumulated += histogram[i];
            if (accumulated >= lowTarget) { low = i; break; }
        }

        int high = 255;
        accumulated = 0;
        for (int i = 255; i >= 0; i--)
        {
            accumulated += histogram[i];
            if (accumulated >= highTarget) { high = i; break; }
        }

        if (high - low < MinStretchRange)
        {
            return (1f, 0f);
        }

        float gain = 255f / (high - low);
        float offset = -(low / 255f) * gain;
        return (gain, offset);
    }

    /// <summary>Colore piu frequente lungo il bordo della regione: e il colore di sfondo con cui riempire il margine.</summary>
    private static Color DominantBorderColor(CapturedImage image, Rectangle region)
    {
        var counts = new Dictionary<int, int>();
        byte[] pixels = image.Bgra;
        int stride = image.Width * 4;

        void Count(int x, int y)
        {
            int index = y * stride + x * 4;
            int key = pixels[index] | (pixels[index + 1] << 8) | (pixels[index + 2] << 16);
            counts[key] = counts.TryGetValue(key, out int current) ? current + 1 : 1;
        }

        int right = region.Right - 1;
        int bottom = region.Bottom - 1;
        for (int x = region.Left; x <= right; x++)
        {
            Count(x, region.Top);
            if (bottom != region.Top) Count(x, bottom);
        }

        for (int y = region.Top + 1; y < bottom; y++)
        {
            Count(region.Left, y);
            if (right != region.Left) Count(right, y);
        }

        int bestKey = 0;
        int bestCount = -1;
        foreach (KeyValuePair<int, int> pair in counts)
        {
            if (pair.Value > bestCount)
            {
                bestCount = pair.Value;
                bestKey = pair.Key;
            }
        }

        return Color.FromArgb(255, (bestKey >> 16) & 0xFF, (bestKey >> 8) & 0xFF, bestKey & 0xFF);
    }

    /// <summary>Applica al colore di sfondo la stessa trasformazione grigi + stiramento usata per l'immagine.</summary>
    private static Color ToStretchedGray(Color color, float gain, float offset)
    {
        float luminance = (0.299f * color.R + 0.587f * color.G + 0.114f * color.B) / 255f;
        float value = Math.Clamp(luminance * gain + offset, 0f, 1f);
        int gray = (int)Math.Round(value * 255f);
        return Color.FromArgb(255, gray, gray, gray);
    }

    private static ColorMatrix GrayscaleMatrix(float gain, float offset)
    {
        float r = 0.299f * gain;
        float g = 0.587f * gain;
        float b = 0.114f * gain;
        return new ColorMatrix(new[]
        {
            new[] { r, r, r, 0f, 0f },
            new[] { g, g, g, 0f, 0f },
            new[] { b, b, b, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { offset, offset, offset, 0f, 1f },
        });
    }
}
