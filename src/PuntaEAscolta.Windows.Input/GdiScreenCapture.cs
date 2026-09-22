using System.ComponentModel;
using System.Runtime.InteropServices;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Windows.Input.Interop;
using static PuntaEAscolta.Windows.Input.Interop.NativeMethods;

namespace PuntaEAscolta.Windows.Input;

/// <summary>
/// Cattura dello schermo con GDI: BitBlt(SRCCOPY | CAPTUREBLT) dal DC dello schermo in una DIB section
/// 32 bpp dall'alto verso il basso. Con DWM attivo il DC dello schermo restituisce il desktop composto
/// (menu, suggerimenti, finestre layered incluse); il cursore non compare mai.
/// Nessuna dipendenza da System.Drawing. Da chiamare sul worker, mai sull'InputThread.
/// </summary>
public sealed unsafe class GdiScreenCapture : IScreenCapture
{
    private const int BytesPerPixel = 4;

    public GdiScreenCapture() { }

    /// <summary>
    /// Cattura <paramref name="desired"/> ritagliato sul monitor che contiene <paramref name="anchor"/>.
    /// Se il rettangolo non tocca affatto quel monitor viene spostato al suo interno mantenendo le dimensioni.
    /// Un rettangolo vuoto produce un'immagine vuota (0x0) senza eccezioni.
    /// </summary>
    public CapturedImage Capture(ScreenRect desired, ScreenPoint anchor)
    {
        nint monitor = MonitorFromPoint(new POINT { X = anchor.X, Y = anchor.Y }, MONITOR_DEFAULTTONEAREST);
        ScreenRect monitorRect = GetMonitorRect(monitor);
        double scale = GetDpiScale(monitor);

        if (desired.IsEmpty)
            return new CapturedImage(Array.Empty<byte>(), 0, 0, default, scale);

        ScreenRect rect = desired.Intersect(monitorRect);
        if (rect.IsEmpty)
            rect = FitInside(desired, monitorRect);
        if (rect.IsEmpty)
            return new CapturedImage(Array.Empty<byte>(), 0, 0, default, scale);

        byte[] bgra = CopyFromScreen(rect);
        return new CapturedImage(bgra, rect.Width, rect.Height, rect, scale);
    }

    public double GetDpiScale(ScreenPoint point)
    {
        nint monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiScale(monitor);
    }

    // ---- interno ---------------------------------------------------------------------------

    private static double GetDpiScale(nint monitor)
    {
        uint dpi = 0;
        if (monitor != 0)
        {
            try
            {
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0) dpi = dpiX;
            }
            catch (EntryPointNotFoundException) { /* shcore assente: si usa il DPI di sistema */ }
        }
        if (dpi == 0) dpi = GetDpiForSystem();
        if (dpi == 0) dpi = 96;
        return dpi / 96.0;
    }

    private static ScreenRect GetMonitorRect(nint monitor)
    {
        if (monitor != 0)
        {
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (GetMonitorInfoW(monitor, ref info))
                return ScreenRect.FromLtrb(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
        }
        // Ripiego: tutto il desktop virtuale.
        return new ScreenRect(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
    }

    /// <summary>Sposta il rettangolo dentro il monitor (riducendolo se più grande del monitor).</summary>
    private static ScreenRect FitInside(ScreenRect rect, ScreenRect bounds)
    {
        int w = Math.Min(rect.Width, bounds.Width);
        int h = Math.Min(rect.Height, bounds.Height);
        if (w <= 0 || h <= 0) return default;
        int x = Math.Clamp(rect.X, bounds.X, bounds.Right - w);
        int y = Math.Clamp(rect.Y, bounds.Y, bounds.Bottom - h);
        return new ScreenRect(x, y, w, h);
    }

    private static byte[] CopyFromScreen(ScreenRect rect)
    {
        nint screenDc = GetDC(0);
        if (screenDc == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDC dello schermo non riuscito");

        nint memDc = 0, dib = 0, oldObject = 0;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == 0) throw new Win32Exception("CreateCompatibleDC non riuscito");

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = rect.Width,
                    biHeight = -rect.Height,   // negativo: righe dall'alto verso il basso
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };
            dib = CreateDIBSection(screenDc, in bmi, DIB_RGB_COLORS, out nint bits, 0, 0);
            if (dib == 0 || bits == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateDIBSection non riuscito");

            oldObject = SelectObject(memDc, dib);
            if (!BitBlt(memDc, 0, 0, rect.Width, rect.Height, screenDc, rect.X, rect.Y, SRCCOPY | CAPTUREBLT))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "BitBlt dallo schermo non riuscito");
            GdiFlush();

            int length = checked(rect.Width * rect.Height * BytesPerPixel);
            var bgra = new byte[length];
            new ReadOnlySpan<byte>((void*)bits, length).CopyTo(bgra);

            // L'alfa letto dallo schermo è 0 o indefinito: si forza opaco.
            for (int i = 3; i < length; i += BytesPerPixel) bgra[i] = 255;
            return bgra;
        }
        finally
        {
            if (memDc != 0)
            {
                if (oldObject != 0) SelectObject(memDc, oldObject);
                DeleteDC(memDc);
            }
            if (dib != 0) DeleteObject(dib);
            ReleaseDC(0, screenDc);
        }
    }
}
