using System.Runtime.InteropServices;
using System.Text;

namespace PuntaEAscolta.Windows.Automation;

/// <summary>Funzioni Win32 di sola lettura usate accanto a UI Automation (finestre, stili, DWM). Nessuna cambia lo stato del sistema.</summary>
internal static class NativeMethods
{
    public const uint GaRoot = 2;
    public const int GwlExStyle = -20;
    public const long WsExTransparent = 0x00000020;
    public const long WsExToolWindow = 0x00000080;
    public const long WsExNoActivate = 0x08000000;
    public const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point pt);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    /// <summary>Nome di classe della finestra, o stringa vuota in caso di errore.</summary>
    public static string GetClassNameSafe(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int n = GetClassName(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : string.Empty;
    }

    /// <summary>True se DWM nasconde la finestra (finestre UWP sospese, popup "visibili" ma non disegnati).</summary>
    public static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            return DwmGetWindowAttribute(hWnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True se la finestra di primo livello sotto il punto non risponde ai messaggi (IsHungAppWindow).</summary>
    public static bool IsWindowAtPointHung(int x, int y)
    {
        IntPtr hwnd = WindowFromPoint(new Point { X = x, Y = y });
        return IsRootWindowHung(hwnd);
    }

    /// <summary>True se la finestra di primo livello dell'app in primo piano non risponde ai messaggi.</summary>
    public static bool IsForegroundWindowHung() => IsRootWindowHung(GetForegroundWindow());

    /// <summary>True se la finestra di primo livello che contiene <paramref name="hwnd"/> non risponde (false per handle nullo).</summary>
    public static bool IsRootWindowHung(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        IntPtr root = GetAncestor(hwnd, GaRoot);
        if (root == IntPtr.Zero) root = hwnd;
        return IsHungAppWindow(root);
    }
}
