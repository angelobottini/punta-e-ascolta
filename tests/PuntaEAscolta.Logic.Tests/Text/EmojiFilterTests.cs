using PuntaEAscolta.Logic.Text;
using Xunit;

namespace PuntaEAscolta.Logic.Tests.Text;

public class EmojiFilterTests
{
    [Theory]
    [InlineData("Ciao 😀 come stai?", "Ciao come stai?")]
    [InlineData("Bravo 👍🏽!", "Bravo!")]
    [InlineData("Famiglia 👨‍👩‍👧‍👦 unita", "Famiglia unita")]
    [InlineData("Italia 🇮🇹 campione", "Italia campione")]
    [InlineData("Premi 1️⃣ per iniziare", "Premi per iniziare")]
    [InlineData("Attenzione ⚠️ pericolo", "Attenzione pericolo")]
    [InlineData("Fatto ✅", "Fatto")]
    [InlineData("Ti voglio bene ❤️", "Ti voglio bene")]
    [InlineData("Stella ⭐ d'oro", "Stella d'oro")]
    public void Strip_RemovesUnicodeEmoji(string input, string expected)
    {
        Assert.Equal(expected, EmojiFilter.Strip(input));
    }

    [Theory]
    [InlineData("Ci vediamo :-) domani", "Ci vediamo domani")]
    [InlineData("Ci vediamo :) domani", "Ci vediamo domani")]
    [InlineData("Grande ;-) davvero", "Grande davvero")]
    [InlineData("Che ridere :D", "Che ridere")]
    [InlineData("Ti amo <3", "Ti amo")]
    [InlineData("Boh ^_^ chissà", "Boh chissà")]
    [InlineData("Peccato :( però", "Peccato però")]
    public void Strip_RemovesIsolatedTextEmoticons(string input, string expected)
    {
        Assert.Equal(expected, EmojiFilter.Strip(input));
    }

    [Theory]
    [InlineData("Alle ore 8:30 in aula")]
    [InlineData("Rapporto a:b uguale a 3:1")]
    [InlineData("Vedi https://esempio.it/pagina")]
    [InlineData("Salva con nome...")]
    [InlineData("Prezzo: 10 euro (sconto)")]
    [InlineData("Testo con parentesi (importante) alla fine")]
    [InlineData("È già così: perché no?")]
    public void Strip_LeavesNormalTextUntouched(string input)
    {
        Assert.Equal(input, EmojiFilter.Strip(input));
    }

    [Fact]
    public void Strip_OnlyEmoji_ReturnsEmpty()
    {
        Assert.Equal("", EmojiFilter.Strip("😀😀😀"));
        Assert.Equal("", EmojiFilter.Strip(":-)"));
    }

    [Fact]
    public void Strip_NullOrEmpty()
    {
        Assert.Equal("", EmojiFilter.Strip(""));
        Assert.Equal("", EmojiFilter.Strip(null!));
    }
}

/// <summary>Correzioni della revisione del 22/09/2026: niente testo vero scambiato per emoticon.</summary>
public class EmojiFilterReviewTests
{
    [Theory]
    [InlineData("Windows XP")]
    [InlineData("8) Salva il file")]
    [InlineData("Unità D: piena")]
    [InlineData("Premi X) per uscire")]
    [InlineData("Confezione x3")]
    [InlineData("Disco locale (D:)")]
    [InlineData(@"Vai a D:\Documenti")]
    public void Strip_KeepsTextThatLooksLikeEmoticons(string input)
    {
        Assert.Equal(input, EmojiFilter.Strip(input));
    }

    [Theory]
    [InlineData("bello xD", "bello")]
    [InlineData("fico 8-) davvero", "fico davvero")]
    [InlineData("ok ;)", "ok")]
    [InlineData("wow :O", "wow")]
    [InlineData("(: ciao", "ciao")]
    public void Strip_StillRemovesRealEmoticons(string input, string expected)
    {
        Assert.Equal(expected, EmojiFilter.Strip(input));
    }
}
