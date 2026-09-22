using PuntaEAscolta.Core;
using PuntaEAscolta.Windows.Input.Interop;

namespace PuntaEAscolta.Windows.Input;

/// <summary>Posizione del puntatore in pixel fisici del desktop virtuale (può essere negativa con più monitor).</summary>
public static class NativePointer
{
    /// <summary>
    /// Usa GetPhysicalCursorPos (coordinate non virtualizzate anche se il processo non fosse DPI-aware),
    /// con ripiego su GetCursorPos. In caso di errore restituisce (0,0).
    /// </summary>
    public static ScreenPoint GetPhysicalPosition()
    {
        if (NativeMethods.GetPhysicalCursorPos(out POINT p) || NativeMethods.GetCursorPos(out p))
            return new ScreenPoint(p.X, p.Y);
        return default;
    }
}
