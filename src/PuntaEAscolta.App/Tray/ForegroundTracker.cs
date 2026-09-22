using static PuntaEAscolta.App.Native.NativeMethods;

namespace PuntaEAscolta.App.Tray;

/// <summary>
/// Ricorda l'ultima finestra in primo piano che non sia la barra delle applicazioni o l'app stessa.
/// Serve solo alla voce di menu "Leggi la selezione": il clic sull'icona di notifica attiva la barra delle applicazioni,
/// quindi prima di leggere si riporta in primo piano la finestra dove l'utente aveva selezionato il testo.
/// L'aggancio (SetWinEventHook fuori contesto) vive sul thread dell'interfaccia, che smista i messaggi.
/// </summary>
internal sealed class ForegroundTracker : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "Progman", "WorkerW",
    };

    private readonly WinEventProc _callback;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private nint _hook;
    private nint _last;

    public ForegroundTracker()
    {
        _callback = OnForegroundChanged;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _callback, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        Consider(GetForegroundWindow());
    }

    /// <summary>Riporta in primo piano l'ultima finestra utile. False se non ce n'è una valida.</summary>
    public bool RestoreLast()
    {
        nint hwnd = _last;
        if (hwnd == 0 || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
        if (GetForegroundWindow() == hwnd) return true;
        return SetForegroundWindow(hwnd);
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        try
        {
            Consider(hwnd);
        }
        catch
        {
            // Mai eccezioni in una callback nativa.
        }
    }

    private void Consider(nint hwnd)
    {
        if (hwnd == 0) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _ownProcessId) return;
        if (ShellClasses.Contains(GetClassName(hwnd))) return;
        _last = hwnd;
    }

    public void Dispose()
    {
        nint hook = _hook;
        _hook = 0;
        if (hook != 0) UnhookWinEvent(hook);
        GC.KeepAlive(_callback);
    }
}
