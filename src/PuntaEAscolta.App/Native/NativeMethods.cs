using System.Runtime.InteropServices;
using System.Text;

namespace PuntaEAscolta.App.Native;

/// <summary>Poche chiamate Win32 usate solo dall'app (console, finestra in primo piano, icone, DPI).</summary>
internal static class NativeMethods
{
    public const int AttachParentProcess = -1;
    public const int StdInputHandle = -10;
    public const int StdOutputHandle = -11;
    public const int StdErrorHandle = -12;
    public const uint Utf8CodePage = 65001;

    public const uint EventSystemForeground = 0x0003;
    public const uint WinEventOutOfContext = 0x0000;
    public const uint WinEventSkipOwnProcess = 0x0002;

    /// <summary>DPI_AWARENESS_PER_MONITOR_AWARE (vale sia per Per-Monitor sia per Per-Monitor V2).</summary>
    public const int DpiAwarenessPerMonitor = 2;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    public static readonly nint DpiContextPerMonitorV2 = -4;

    public delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll")]
    public static extern uint GetFileType(nint handle);

    [DllImport("kernel32.dll")]
    public static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll")]
    public static extern nint GetConsoleWindow();

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hwnd);

    /// <summary>ASFW_ANY: consente a un altro processo di portare in primo piano una sua finestra.</summary>
    public const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(nint hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    public static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);

    public static string GetClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        int n = GetClassNameW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : "";
    }
}
