using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Core.Abstractions;

public enum TriggerButton { None = 0, Middle = 1, X1 = 2, X2 = 3 }

public enum HotkeyAction { ReadAtPointer, ReadSelection, Stop, ToggleDictation, TogglePause }

/// <summary>Evento grezzo prodotto dallo strato di piattaforma. TimestampMs è monotono (Environment.TickCount64).</summary>
public abstract record InputEvent(long TimestampMs);

/// <summary>Pressione o rilascio del pulsante del mouse configurato come attivatore (già "inghiottito" dalla piattaforma).</summary>
public sealed record TriggerButtonEvent(bool IsDown, ScreenPoint Point, long TimestampMs) : InputEvent(TimestampMs);

/// <summary>Scorciatoia da tastiera globale. Point è la posizione fisica del puntatore in quell'istante.</summary>
public sealed record HotkeyEvent(HotkeyAction Action, ScreenPoint Point, long TimestampMs) : InputEvent(TimestampMs);

/// <summary>
/// Sorgente di input globale (Windows: WH_MOUSE_LL + RegisterHotKey su un thread dedicato).
/// L'evento Input viene sollevato da un thread NON UI e non deve mai bloccare: i gestori devono solo accodare.
/// </summary>
public interface IInputSource : IDisposable
{
    event Action<InputEvent>? Input;

    /// <summary>Avvia hook e scorciatoie. Gli eventuali problemi (es. scorciatoia già occupata) finiscono in LastProblems.</summary>
    void Start(InputSettings input, DictationSettings dictation);

    /// <summary>Applica nuove impostazioni a caldo.</summary>
    void Apply(InputSettings input, DictationSettings dictation);

    /// <summary>Attiva il tasto Esc come "ferma la voce" SOLO mentre la voce parla (altrimenti Esc verrebbe sottratto a tutto il sistema).</summary>
    void SetStopKeyActive(bool active);

    /// <summary>Sospende o riprende l'intercettazione dell'attivatore (pausa dal menu dell'icona di notifica).</summary>
    void SetPaused(bool paused);

    IReadOnlyList<string> LastProblems { get; }
}

/// <summary>Scorciatoia da tastiera in forma testuale, es. "Win+Shift+A", "Ctrl+F9", "F8". Stringa vuota = nessuna.</summary>
public sealed record HotkeyGesture(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, alt = false, shift = false, win = false;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": case "maiusc": shift = true; break;
                case "win": case "windows": win = true; break;
                default:
                    if (key is not null) return false;
                    key = raw;
                    break;
            }
        }
        if (key is null) return false;
        gesture = new HotkeyGesture(ctrl, alt, shift, win, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }
}
