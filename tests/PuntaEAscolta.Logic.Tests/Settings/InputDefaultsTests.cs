using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Settings;
using Xunit;

namespace PuntaEAscolta.Logic.Tests.Settings;

/// <summary>
/// Prove dal vivo del 23/09/2026 su un portatile con il solo touchpad: senza rotellina e senza scorciatoia per il puntatore
/// non si poteva leggere nulla sotto il puntatore.
/// </summary>
public class InputDefaultsTests
{
    [Fact]
    public void ReadAtPointer_HasADefaultHotkey_CtrlShiftSpace()
    {
        var input = new InputSettings();

        Assert.Equal("Ctrl+Shift+Space", input.HotkeyReadAtPointer);
        Assert.True(HotkeyGesture.TryParse(input.HotkeyReadAtPointer, out var gesture));
        Assert.NotNull(gesture);
        Assert.True(gesture!.Ctrl);
        Assert.True(gesture.Shift);
        // Alt e Win chiuderebbero un menu aperto: niente Alt, niente Win.
        Assert.False(gesture.Alt);
        Assert.False(gesture.Win);
        Assert.Equal("Space", gesture.Key);
    }

    [Theory]
    [InlineData("Ctrl+Maiusc+Spazio", "Spazio")]
    [InlineData("ctrl + shift + space", "space")]
    [InlineData("Control+Shift+Space", "Space")]
    public void ItalianAndSpacedForms_AreParsed(string text, string key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.True(gesture!.Ctrl);
        Assert.True(gesture.Shift);
        Assert.Equal(key, gesture.Key);
    }

    [Fact]
    public void DefaultHotkeys_DoNotCollide()
    {
        var settings = new AppSettings();
        var texts = new[] { settings.Input.HotkeyReadAtPointer, settings.Input.HotkeyReadSelection, settings.Dictation.Hotkey };
        var parsed = texts.Select(t => HotkeyGesture.TryParse(t, out var g) ? g!.ToString().ToLowerInvariant() : t).ToList();
        Assert.Equal(parsed.Count, parsed.Distinct().Count());
    }

    [Fact]
    public void InjectedClicks_AreAcceptedByDefault()
    {
        // Il tocco a tre dita del touchpad come "pulsante centrale" e i programmi di assistenza remota generano clic "iniettati".
        Assert.True(new InputSettings().AcceptInjectedEvents);
    }

    [Fact]
    public void Defaults_SurviveASaveAndLoadRoundTrip()
    {
        var copy = JsonSettingsStore.Clone(new AppSettings());

        Assert.Equal("Ctrl+Shift+Space", copy.Input.HotkeyReadAtPointer);
        Assert.True(copy.Input.AcceptInjectedEvents);
    }
}
