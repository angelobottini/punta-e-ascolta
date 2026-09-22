using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Windows.Input.Interop;

namespace PuntaEAscolta.Windows.Input;

/// <summary>Traduce i nomi dei tasti di <see cref="HotkeyGesture"/> in codici tasto virtuali e modificatori di RegisterHotKey.</summary>
internal static class HotkeyMap
{
    /// <summary>Nomi accettati (senza distinzione fra maiuscole e minuscole), in inglese e in italiano.</summary>
    private static readonly Dictionary<string, (uint Vk, string Canonical)> s_named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = (0x20, "Space"), ["spazio"] = (0x20, "Space"),
        ["esc"] = (0x1B, "Esc"), ["escape"] = (0x1B, "Esc"),
        ["enter"] = (0x0D, "Enter"), ["return"] = (0x0D, "Enter"), ["invio"] = (0x0D, "Enter"),
        ["tab"] = (0x09, "Tab"),
        ["backspace"] = (0x08, "Backspace"), ["back"] = (0x08, "Backspace"),
        ["left"] = (0x25, "Left"), ["sinistra"] = (0x25, "Left"),
        ["up"] = (0x26, "Up"), ["su"] = (0x26, "Up"),
        ["right"] = (0x27, "Right"), ["destra"] = (0x27, "Right"),
        ["down"] = (0x28, "Down"), ["giu"] = (0x28, "Down"), ["giù"] = (0x28, "Down"),
        ["pageup"] = (0x21, "PageUp"), ["pgup"] = (0x21, "PageUp"), ["pagsu"] = (0x21, "PageUp"),
        ["pagedown"] = (0x22, "PageDown"), ["pgdn"] = (0x22, "PageDown"), ["paggiu"] = (0x22, "PageDown"), ["paggiù"] = (0x22, "PageDown"),
        ["home"] = (0x24, "Home"),
        ["end"] = (0x23, "End"), ["fine"] = (0x23, "End"),
        ["insert"] = (0x2D, "Insert"), ["ins"] = (0x2D, "Insert"),
        ["delete"] = (0x2E, "Delete"), ["del"] = (0x2E, "Delete"), ["canc"] = (0x2E, "Delete"),
        ["pause"] = (0x13, "Pause"), ["pausa"] = (0x13, "Pause"),
        ["printscreen"] = (0x2C, "PrintScreen"), ["stamp"] = (0x2C, "PrintScreen"), ["snapshot"] = (0x2C, "PrintScreen"),
        ["scrolllock"] = (0x91, "ScrollLock"), ["blocscorr"] = (0x91, "ScrollLock"),
        ["numlock"] = (0x90, "NumLock"), ["blocnum"] = (0x90, "NumLock"),
        ["capslock"] = (0x14, "CapsLock"), ["blocmaiusc"] = (0x14, "CapsLock"),
        ["apps"] = (0x5D, "Apps"), ["menu"] = (0x5D, "Apps"),
        ["num0"] = (0x60, "Num0"), ["num1"] = (0x61, "Num1"), ["num2"] = (0x62, "Num2"), ["num3"] = (0x63, "Num3"),
        ["num4"] = (0x64, "Num4"), ["num5"] = (0x65, "Num5"), ["num6"] = (0x66, "Num6"), ["num7"] = (0x67, "Num7"),
        ["num8"] = (0x68, "Num8"), ["num9"] = (0x69, "Num9"),
        ["nummultiply"] = (0x6A, "NumMultiply"), ["num*"] = (0x6A, "NumMultiply"),
        ["numadd"] = (0x6B, "NumAdd"), ["numplus"] = (0x6B, "NumAdd"),
        ["numsubtract"] = (0x6D, "NumSubtract"), ["numminus"] = (0x6D, "NumSubtract"),
        ["numdecimal"] = (0x6E, "NumDecimal"),
        ["numdivide"] = (0x6F, "NumDivide"), ["num/"] = (0x6F, "NumDivide"),
        ["volumemute"] = (0xAD, "VolumeMute"), ["volumedown"] = (0xAE, "VolumeDown"), ["volumeup"] = (0xAF, "VolumeUp"),
        ["medianext"] = (0xB0, "MediaNext"), ["mediaprev"] = (0xB1, "MediaPrev"), ["mediaprevious"] = (0xB1, "MediaPrev"),
        ["mediastop"] = (0xB2, "MediaStop"), ["mediaplaypause"] = (0xB3, "MediaPlayPause"),
        // Tasti OEM per nome (posizione fisica, indipendente dal layout)
        ["oem1"] = (0xBA, "Oem1"), ["oemplus"] = (0xBB, "OemPlus"), ["oemcomma"] = (0xBC, "OemComma"),
        ["oemminus"] = (0xBD, "OemMinus"), ["oemperiod"] = (0xBE, "OemPeriod"), ["oem2"] = (0xBF, "Oem2"),
        ["oem3"] = (0xC0, "Oem3"), ["oem4"] = (0xDB, "Oem4"), ["oem5"] = (0xDC, "Oem5"), ["oem6"] = (0xDD, "Oem6"),
        ["oem7"] = (0xDE, "Oem7"), ["oem8"] = (0xDF, "Oem8"), ["oem102"] = (0xE2, "Oem102"),
    };

    /// <summary>
    /// Riconosce lettere A-Z, cifre 0-9, F1-F24, i nomi della tabella e, come ultima risorsa,
    /// un singolo carattere del layout corrente tramite VkKeyScanW (es. "ò", "+").
    /// </summary>
    internal static bool TryGetVirtualKey(string key, out uint vk, out string canonical)
    {
        vk = 0;
        canonical = key;
        if (string.IsNullOrWhiteSpace(key)) return false;
        key = key.Trim();

        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' || c is >= '0' and <= '9')
            {
                vk = c;
                canonical = c.ToString();
                return true;
            }
        }

        if ((key[0] == 'F' || key[0] == 'f') && key.Length is >= 2 and <= 3 &&
            int.TryParse(key.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            canonical = "F" + fn;
            return true;
        }

        if (s_named.TryGetValue(key, out var entry))
        {
            vk = entry.Vk;
            canonical = entry.Canonical;
            return true;
        }

        if (key.Length == 1)
        {
            // Carattere del layout corrente: il byte basso è il tasto virtuale (-1 = non presente).
            short scan = NativeMethods.VkKeyScanW(key[0]);
            if (scan != -1 && (scan & 0xFF) != 0xFF)
            {
                vk = (uint)(scan & 0xFF);
                canonical = key;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Una scorciatoia registrata con RegisterHotKey viene tolta a TUTTI i programmi finché l'app è aperta (e scatta anche
    /// sui tasti iniettati dalla dettatura). Restituisce il motivo per cui la combinazione non va registrata, oppure null:
    /// Esc mai (lo registra l'app solo mentre la voce parla); senza Ctrl, Alt o Win (Maiusc da solo non basta: Maiusc+A è
    /// la A maiuscola) solo F1-F24, Pausa, Bloc Scorr e i tasti multimediali.
    /// </summary>
    internal static string? CheckGlobalSafety(HotkeyGesture gesture, uint vk)
    {
        if (vk == VkEscape)
            return "Esc non può essere una scorciatoia fissa: ferma già la voce mentre parla (opzione \"Esc ferma la voce\")";
        if (gesture.Ctrl || gesture.Alt || gesture.Win) return null;
        if (IsStandaloneKey(vk)) return null;
        return "senza Ctrl, Alt o Win sono ammessi solo i tasti da F1 a F24, Pausa, Bloc Scorr e i tasti multimediali: " +
               "altrimenti il tasto non funzionerebbe più in nessun programma";
    }

    private const uint VkEscape = 0x1B;

    /// <summary>F1-F24, Pausa, Bloc Scorr, volume e tasti multimediali: nessun programma li usa per scrivere.</summary>
    private static bool IsStandaloneKey(uint vk) => vk is >= 0x70 and <= 0x87 or 0x13 or 0x91 or >= 0xAD and <= 0xB3;

    /// <summary>Controllo completo del testo di una scorciatoia: null se va bene (o se è vuota), altrimenti il problema in italiano.</summary>
    internal static string? Validate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!HotkeyGesture.TryParse(text, out HotkeyGesture? gesture) || gesture is null || !TryGetVirtualKey(gesture.Key, out uint vk, out _))
            return $"scorciatoia \"{text.Trim()}\" non riconosciuta";
        return CheckGlobalSafety(gesture, vk);
    }

    internal static uint ToModifiers(HotkeyGesture gesture)
    {
        uint mods = NativeMethods.MOD_NOREPEAT;
        if (gesture.Ctrl) mods |= NativeMethods.MOD_CONTROL;
        if (gesture.Alt) mods |= NativeMethods.MOD_ALT;
        if (gesture.Shift) mods |= NativeMethods.MOD_SHIFT;
        if (gesture.Win) mods |= NativeMethods.MOD_WIN;
        return mods;
    }
}
