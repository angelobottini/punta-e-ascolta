using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Windows.Input;
using Xunit;

namespace PuntaEAscolta.Windows.Tests;

/// <summary>Revisione del 22/09/2026 (docs/impl-notes/revisione.md): scorciatoie che toglierebbero un tasto a tutto il sistema.</summary>
public class HotkeySafetyTests
{
    [Theory]
    [InlineData("Esc")]
    [InlineData("Escape")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Invio")]
    [InlineData("Spazio")]
    [InlineData("Tab")]
    [InlineData("Backspace")]
    [InlineData("A")]
    [InlineData("Shift+A")]
    [InlineData("Maiusc+Canc")]
    [InlineData("5")]
    [InlineData("Num5")]
    [InlineData("CapsLock")]
    public void Validate_RejectsKeysThatWouldBeStolenFromEveryProgram(string text)
    {
        Assert.NotNull(WindowsInputSource.ValidateHotkey(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("F8")]
    [InlineData("Shift+F9")]
    [InlineData("F24")]
    [InlineData("Pausa")]
    [InlineData("ScrollLock")]
    [InlineData("MediaPlayPause")]
    [InlineData("Win+Shift+F9")]
    [InlineData("Win+Shift+D")]
    [InlineData("Ctrl+F8")]
    [InlineData("Alt+Q")]
    [InlineData("Win+Invio")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("Ctrl+Maiusc+Spazio")]
    [InlineData("ctrl + shift + space")]
    [InlineData("Ctrl+Shift+0")]
    public void Validate_AcceptsSafeHotkeys(string text)
    {
        Assert.Null(WindowsInputSource.ValidateHotkey(text));
    }

    [Fact]
    public void DefaultReadAtPointerHotkey_IsValid_AndMapsToCtrlShiftSpace()
    {
        // Prove dal vivo del 23/09/2026: portatile con il solo touchpad, serve una scorciatoia di serie per il puntatore.
        string text = new PuntaEAscolta.Core.Settings.InputSettings().HotkeyReadAtPointer;
        Assert.Null(WindowsInputSource.ValidateHotkey(text));

        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.True(HotkeyMap.TryGetVirtualKey(gesture!.Key, out uint vk, out string canonical));
        Assert.Equal(0x20u, vk);
        Assert.Equal("Space", canonical);
        uint mods = HotkeyMap.ToModifiers(gesture);
        Assert.Equal(PuntaEAscolta.Windows.Input.Interop.NativeMethods.MOD_CONTROL | PuntaEAscolta.Windows.Input.Interop.NativeMethods.MOD_SHIFT
                     | PuntaEAscolta.Windows.Input.Interop.NativeMethods.MOD_NOREPEAT, mods);
    }

    [Theory]
    [InlineData("Space")]
    [InlineData("space")]
    [InlineData("Spazio")]
    [InlineData("SPAZIO")]
    public void SpaceKey_IsRecognisedInEnglishAndItalian(string key)
    {
        Assert.True(HotkeyMap.TryGetVirtualKey(key, out uint vk, out string canonical));
        Assert.Equal(0x20u, vk);
        Assert.Equal("Space", canonical);
    }

    [Fact]
    public void Validate_UnknownKey_IsReported()
    {
        var problem = WindowsInputSource.ValidateHotkey("Ctrl+Pippo");
        Assert.NotNull(problem);
        Assert.Contains("non riconosciuta", problem);
    }

    [Fact]
    public void CheckGlobalSafety_EscIsAlwaysRejected()
    {
        Assert.NotNull(HotkeyMap.CheckGlobalSafety(new HotkeyGesture(true, false, false, true, "Esc"), 0x1B));
        Assert.Null(HotkeyMap.CheckGlobalSafety(new HotkeyGesture(false, false, false, false, "F8"), 0x77));
    }
}

/// <summary>
/// Prove dal vivo del 23/09/2026: i tasti della scorciatoia restavano premuti più di un secondo e il Ctrl+C della selezione
/// non partiva mai. L'attesa è di 3 s e la fase "appunti" del risolutore deve durare di più.
/// </summary>
public class ClipboardTimeoutBudgetTests
{
    /// <summary>Margine per l'avvio del thread STA, la fotografia degli appunti e la lettura del testo copiato.</summary>
    private const int MarginMs = 500;

    [Fact]
    public void ModifierWait_IsAtLeastThreeSeconds()
    {
        Assert.True(PuntaEAscolta.Windows.Input.Interop.InputInjection.ModifierReleaseTimeoutMs >= 3000);
    }

    [Fact]
    public void WorstCaseBeforeTheCopy_StaysBelowTheResolverClipboardTimeout()
    {
        int worst = ClipboardSelectionReader.WorstCaseBeforeCopyMs;
        Assert.Equal(2 * ClipboardSelectionReader.OpenRetries * ClipboardSelectionReader.OpenRetryDelayMs
                     + PuntaEAscolta.Windows.Input.Interop.InputInjection.ModifierReleaseTimeoutMs + ClipboardSelectionReader.CopyTimeoutMs, worst);
        Assert.True(worst + MarginMs <= PuntaEAscolta.Logic.Reading.TextResolver.DefaultClipboardTimeoutMs,
            $"caso peggiore {worst} ms + margine {MarginMs} ms, fase appunti {PuntaEAscolta.Logic.Reading.TextResolver.DefaultClipboardTimeoutMs} ms");
    }
}

/// <summary>
/// "Leggi la selezione" senza selezione passa al puntatore, quindi si preme anche senza aver selezionato niente: con un
/// terminale in primo piano il Ctrl+C simulato interromperebbe il programma in esecuzione, e non si invia.
/// </summary>
public class ClipboardTerminalGuardTests
{
    [Theory]
    [InlineData("ConsoleWindowClass")]              // console di Windows (conhost)
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS")]   // Windows Terminal
    [InlineData("PseudoConsoleWindow")]
    [InlineData("VirtualConsoleClass")]             // ConEmu
    [InlineData("mintty")]                          // Git Bash
    [InlineData("consolewindowclass")]              // i nomi di classe non distinguono maiuscole e minuscole
    public void Terminals_GetNoCtrlC(string className)
    {
        Assert.True(ClipboardSelectionReader.IsTerminalWindowClass(className));
    }

    [Theory]
    [InlineData("OpusApp")]            // Word
    [InlineData("XLMAIN")]             // Excel
    [InlineData("Notepad")]
    [InlineData("Chrome_WidgetWin_1")] // browser, VS Code
    [InlineData("WindowsForms10.Window.8.app.0.2bf8098_r6_ad1")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherWindows_GetTheCtrlC(string? className)
    {
        Assert.False(ClipboardSelectionReader.IsTerminalWindowClass(className));
    }
}

/// <summary>Clic dell'attivatore generati da software e ignorati: avviso nel registro al massimo una volta al minuto.</summary>
public class InjectedClickNoticeTests
{
    [Fact]
    public void Notice_IsRateLimitedToOncePerMinute()
    {
        long last = long.MinValue;
        long interval = WindowsInputSource.InjectedIgnoredNoticeIntervalMs;
        Assert.Equal(60_000, interval);

        Assert.True(WindowsInputSource.ShouldNotify(1_000, ref last, interval));
        Assert.Equal(1_000, last);
        Assert.False(WindowsInputSource.ShouldNotify(2_000, ref last, interval));
        Assert.False(WindowsInputSource.ShouldNotify(60_999, ref last, interval));
        Assert.Equal(1_000, last);
        Assert.True(WindowsInputSource.ShouldNotify(61_000, ref last, interval));
        Assert.Equal(61_000, last);
    }

    [Fact]
    public void Notice_FirstOneIsAlwaysAllowed_EvenAtTickZero()
    {
        long last = long.MinValue;
        Assert.True(WindowsInputSource.ShouldNotify(0, ref last, 60_000));
    }

    [Fact]
    public void Notice_TellsWhatToEnable()
    {
        Assert.Contains("generato da software", WindowsInputSource.InjectedIgnoredMessage);
        Assert.Contains("tre dita", WindowsInputSource.InjectedIgnoredMessage);
        Assert.Contains("'Accetta i clic generati da software'", WindowsInputSource.InjectedIgnoredMessage);
    }
}

public class ClipboardFormatPolicyTests
{
    [Theory]
    [InlineData(13u, null)]   // CF_UNICODETEXT
    [InlineData(1u, null)]    // CF_TEXT
    [InlineData(7u, null)]    // CF_OEMTEXT
    [InlineData(16u, null)]   // CF_LOCALE
    [InlineData(0xC0A1u, "HTML Format")]
    [InlineData(0xC0A2u, "Rich Text Format")]
    [InlineData(0xC0A3u, "CanIncludeInClipboardHistory")]
    [InlineData(0xC0A4u, "Chromium internal source URL")]
    public void TextFormats_ArePreserved(uint format, string? name)
    {
        Assert.True(ClipboardFormatPolicy.IsPreservableText(format, name));
    }

    [Theory]
    [InlineData(2u, null)]    // CF_BITMAP
    [InlineData(8u, null)]    // CF_DIB
    [InlineData(17u, null)]   // CF_DIBV5
    [InlineData(14u, null)]   // CF_ENHMETAFILE
    [InlineData(15u, null)]   // CF_HDROP
    [InlineData(0xC0B0u, "PNG")]
    [InlineData(0xC0B1u, "Biff12")]
    [InlineData(0xC0B2u, "Object Descriptor")]
    [InlineData(0xC0B3u, "Embed Source")]
    [InlineData(0xC0B4u, null)]
    [InlineData(0x0200u, null)]  // CF_PRIVATEFIRST
    public void OtherFormats_MakeTheReaderGiveUp(uint format, string? name)
    {
        Assert.False(ClipboardFormatPolicy.IsPreservableText(format, name));
    }

    [Fact]
    public void AnsiAndOemText_AreRegeneratedByWindows()
    {
        Assert.True(ClipboardFormatPolicy.IsSynthesized(1));
        Assert.True(ClipboardFormatPolicy.IsSynthesized(7));
        Assert.False(ClipboardFormatPolicy.IsSynthesized(13));
        Assert.False(ClipboardFormatPolicy.IsSynthesized(16));
    }
}
