using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Speech.Dictation;
using Xunit;

namespace PuntaEAscolta.Speech.Tests;

public sealed class VoiceCommandsTests
{
    private static readonly DictationSettings Defaults = new();

    [Theory]
    [InlineData("cancella", VoiceCommand.Delete)]
    [InlineData("Cancella.", VoiceCommand.Delete)]
    [InlineData("  CANCELLA   TUTTO! ", VoiceCommand.Delete)]
    [InlineData("a capo", VoiceCommand.NewLine)]
    [InlineData("Vai a capo.", VoiceCommand.NewLine)]
    [InlineData("A-capo", VoiceCommand.NewLine)]
    [InlineData("Rileggi?", VoiceCommand.ReadAgain)]
    [InlineData("cancella la riga", VoiceCommand.None)]
    [InlineData("ho detto a capo", VoiceCommand.None)]
    [InlineData("", VoiceCommand.None)]
    [InlineData("...", VoiceCommand.None)]
    [InlineData(null, VoiceCommand.None)]
    public void Whole_utterance_matching(string? utterance, VoiceCommand expected)
    {
        Assert.Equal(expected, VoiceCommands.Match(utterance, Defaults));
    }

    [Fact]
    public void Aliases_from_settings_ignore_accents_and_punctuation()
    {
        var settings = new DictationSettings
        {
            CommandDelete = ["cancela", "Perché no"],
            CommandNewLine = [],
            CommandReadAgain = ["ripeti!"]
        };

        Assert.Equal(VoiceCommand.Delete, VoiceCommands.Match("Cancela", settings));
        Assert.Equal(VoiceCommand.Delete, VoiceCommands.Match("perche no", settings));
        Assert.Equal(VoiceCommand.ReadAgain, VoiceCommands.Match("Ripeti.", settings));
        Assert.Equal(VoiceCommand.None, VoiceCommands.Match("a capo", settings));
        Assert.True(VoiceCommands.TryMatch("ripeti", settings, out var command));
        Assert.Equal(VoiceCommand.ReadAgain, command);
    }

    [Fact]
    public void Normalize_is_lowercase_without_accents_and_single_spaced()
    {
        Assert.Equal("citta e perche", VoiceCommands.Normalize("  Città, È... perché?! "));
        Assert.Equal("", VoiceCommands.Normalize(" \t "));
    }
}
