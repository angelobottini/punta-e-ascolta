using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Text;
using Xunit;

namespace PuntaEAscolta.Logic.Tests.Text;

public class LanguageGuesserTests
{
    [Theory]
    [InlineData("File")]
    [InlineData("Salva con nome")]
    [InlineData("Apri Recenti")]
    [InlineData("Il sig. Rossi arriva alle 15.30.")]
    [InlineData("Nuovo documento")]
    [InlineData("Livelli")]
    [InlineData("Opacità")]
    [InlineData("Esci")]
    [InlineData("Cella vuota")]
    [InlineData("Incolla, altre opzioni")]
    public void Guess_Italian(string text) => Assert.Equal("it", LanguageGuesser.Guess(text));

    [Theory]
    [InlineData("Save As")]
    [InlineData("Layer Effects")]
    [InlineData("Export Persona")]
    [InlineData("Adjustment Layers")]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    [InlineData("Brush Width")]
    [InlineData("Undo History")]
    public void Guess_English(string text) => Assert.Equal("en", LanguageGuesser.Guess(text));

    [Fact]
    public void Guess_NoLetters_ReturnsNull()
    {
        Assert.Null(LanguageGuesser.Guess("123"));
        Assert.Null(LanguageGuesser.Guess("..."));
        Assert.Null(LanguageGuesser.Guess(""));
    }

    [Fact]
    public void Guess_ForcedModes()
    {
        Assert.Equal("it", LanguageGuesser.Guess("Save As", LabelLanguageMode.Italian));
        Assert.Equal("en", LanguageGuesser.Guess("Salva", LabelLanguageMode.English));
    }

    [Fact]
    public void Guess_AmbiguousDefaultsToItalian()
    {
        Assert.Equal("it", LanguageGuesser.Guess("Zoom"));
        Assert.Equal("it", LanguageGuesser.Guess("Ok"));
    }
}
