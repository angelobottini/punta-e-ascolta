using System.Runtime.InteropServices;

namespace PuntaEAscolta.Windows.Input.Interop;

// Strutture Win32. x64 e ARM64 sono entrambi LLP64: stessi layout e stesse dimensioni
// (int/uint a 32 bit, puntatori e nint/nuint a 64 bit). Nessuna ipotesi su IntPtr.Size.

/// <summary>Coordinate con segno: con più monitor possono essere negative.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary>Dati dell'hook WH_MOUSE_LL (32 byte a 64 bit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    /// <summary>Coordinate schermo per-monitor-aware (pixel fisici del desktop virtuale).</summary>
    public POINT pt;
    /// <summary>HIWORD = XBUTTON1 (1) o XBUTTON2 (2) per WM_XBUTTON*, altrimenti delta della rotella.</summary>
    public uint mouseData;
    /// <summary>LLMHF_INJECTED = 0x1, LLMHF_LOWER_IL_INJECTED = 0x2.</summary>
    public uint flags;
    public uint time;
    /// <summary>ULONG_PTR: qui gli eventi generati da noi portano la firma InputInjection.Tag.</summary>
    public nuint dwExtraInfo;
}

/// <summary>Messaggio della coda del thread (48 byte a 64 bit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nuint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
}

/// <summary>cbSize = 40.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

/// <summary>40 byte.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    /// <summary>Negativo = bitmap dall'alto verso il basso.</summary>
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    public uint bmiColors;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LASTINPUTINFO
{
    public uint cbSize;
    public uint dwTime;
}

/// <summary>24 byte a 64 bit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public nuint dwExtraInfo;
}

/// <summary>32 byte a 64 bit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HARDWAREINPUT
{
    public uint uMsg;
    public ushort wParamL;
    public ushort wParamH;
}

/// <summary>Unione dei tre tipi di input: la dimensione è quella di MOUSEINPUT (32 byte).</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

/// <summary>40 byte a 64 bit: type (4) + riempimento (4) + unione (32).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct INPUT
{
    public uint type;
    public INPUTUNION u;
}

/// <summary>80 byte a 64 bit. lpfnWndProc è un puntatore a funzione unmanaged passato come nint.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public char* lpszMenuName;
    public char* lpszClassName;
    public nint hIconSm;
}
