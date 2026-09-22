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
    public void Validate_AcceptsSafeHotkeys(string text)
    {
        Assert.Null(WindowsInputSource.ValidateHotkey(text));
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
