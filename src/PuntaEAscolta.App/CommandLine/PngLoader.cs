using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.App.CommandLine;

/// <summary>Carica un'immagine (PNG, ma anche JPEG o BMP) con GDI+ e la converte nel formato della cattura dello schermo.</summary>
internal static class PngLoader
{
    public static CapturedImage Load(string path, double dpiScale)
    {
        using var source = new Bitmap(path);
        int width = source.Width;
        int height = source.Height;
        var bgra = new byte[checked(width * height * 4)];

        var rect = new Rectangle(0, 0, width, height);
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = width * 4;
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            source.UnlockBits(data);
        }

        // Come la cattura GDI: alfa sempre pieno (i PNG con trasparenza diventerebbero neri per alcuni motori).
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;

        return new CapturedImage(bgra, width, height, new ScreenRect(0, 0, width, height), dpiScale);
    }
}
