using System.Runtime.InteropServices;
using static PuntaEAscolta.Windows.Input.Interop.NativeMethods;

namespace PuntaEAscolta.Windows.Input.Interop;

/// <summary>
/// Costruzione e invio di eventi SendInput. Ogni evento porta la firma <see cref="Tag"/> in dwExtraInfo,
/// così l'hook del mouse riconosce e ignora gli eventi generati dall'app stessa.
/// </summary>
internal static unsafe class InputInjection
{
    /// <summary>"PEA1": firma dei nostri SendInput.</summary>
    internal const nuint Tag = 0x50454131;

    /// <summary>Tempo massimo di attesa perché l'utente rilasci i modificatori prima di inviare tasti.</summary>
    internal const int ModifierReleaseTimeoutMs = 1000;

    internal static int InputSize => sizeof(INPUT);

    internal static INPUT KeyDown(ushort vk)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk = vk;
        input.u.ki.wScan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
        input.u.ki.dwFlags = 0;
        input.u.ki.dwExtraInfo = Tag;
        return input;
    }

    internal static INPUT KeyUp(ushort vk)
    {
        var input = KeyDown(vk);
        input.u.ki.dwFlags = KEYEVENTF_KEYUP;
        return input;
    }

    internal static INPUT UnicodeDown(char unit)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk = 0;
        input.u.ki.wScan = unit;
        input.u.ki.dwFlags = KEYEVENTF_UNICODE;
        input.u.ki.dwExtraInfo = Tag;
        return input;
    }

    internal static INPUT UnicodeUp(char unit)
    {
        var input = UnicodeDown(unit);
        input.u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        return input;
    }

    /// <summary>
    /// Invia un blocco di eventi. Restituisce quanti sono stati accettati; se meno del richiesto,
    /// <paramref name="lastError"/> contiene il codice Win32 (tipico: finestra elevata in primo piano, UIPI).
    /// </summary>
    internal static int Send(ReadOnlySpan<INPUT> inputs, out int lastError)
    {
        lastError = 0;
        if (inputs.IsEmpty) return 0;
        fixed (INPUT* p = inputs)
        {
            uint sent = SendInput((uint)inputs.Length, p, sizeof(INPUT));
            if (sent < (uint)inputs.Length) lastError = Marshal.GetLastPInvokeError();
            return (int)sent;
        }
    }

    /// <summary>
    /// Attende (al massimo timeoutMs) che Ctrl, Alt, Shift e Win siano rilasciati: subito dopo una scorciatoia
    /// i modificatori sono ancora premuti e corromperebbero il testo o il Ctrl+C simulato.
    /// </summary>
    internal static bool WaitForModifiersRelease(int timeoutMs, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (AnyModifierDown())
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    internal static bool AnyModifierDown() =>
        IsKeyDown(VK_SHIFT) || IsKeyDown(VK_CONTROL) || IsKeyDown(VK_MENU) || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);

    internal static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
