using PuntaEAscolta.Logic.Text;
using Xunit;

namespace PuntaEAscolta.Logic.Tests.Text;

public class SentenceSplitterTests
{
    private const string Paragraph = "Il sig. Rossi arriva alle 15.30. Porta con sé 3,5 kg di mele! Va bene? Costa 1.000 euro ecc. ma non importa. Fine.\r";

    [Theory]
    [InlineData(3, "Il sig. Rossi arriva alle 15.30.")]
    [InlineData(20, "Il sig. Rossi arriva alle 15.30.")]
    [InlineData(31, "Il sig. Rossi arriva alle 15.30.")]
    [InlineData(34, "Porta con sé 3,5 kg di mele!")]
    [InlineData(60, "Porta con sé 3,5 kg di mele!")]
    [InlineData(63, "Va bene?")]
    [InlineData(75, "Costa 1.000 euro ecc. ma non importa.")]
    [InlineData(92, "Costa 1.000 euro ecc. ma non importa.")]
    [InlineData(112, "Fine.")]
    public void ExtractSentence_ReturnsSentenceContainingOffset(int offset, string expected)
    {
        Assert.Equal(expected, SentenceSplitter.ExtractSentence(Paragraph, offset));
    }

    [Fact]
    public void ExtractSentence_OnSpaceBetweenSentences_PrefersNext()
    {
        // indice 32 è lo spazio dopo "15.30."
        Assert.Equal("Porta con sé 3,5 kg di mele!", SentenceSplitter.ExtractSentence(Paragraph, 32));
    }

    [Fact]
    public void ExtractSentence_ClampsOffset()
    {
        Assert.Equal("Il sig. Rossi arriva alle 15.30.", SentenceSplitter.ExtractSentence(Paragraph, -5));
        Assert.Equal("Fine.", SentenceSplitter.ExtractSentence(Paragraph, 10_000));
    }

    [Fact]
    public void ExtractSentence_RemovesWordControlCharacters()
    {
        var s = SentenceSplitter.ExtractSentence("Prima riga\vseconda parte.\r", 3);
        Assert.Equal("Prima riga", s);
        var cell = SentenceSplitter.ExtractSentence("Contenuto cella\a", 2);
        Assert.Equal("Contenuto cella", cell);
        var obj = SentenceSplitter.ExtractSentence("Vedi figura ￼ qui sotto. Altro.", 2);
        Assert.Equal("Vedi figura qui sotto.", obj);
    }

    [Fact]
    public void ExtractSentence_EndOfDocumentWithoutParagraphMark()
    {
        Assert.Equal("Ultima frase senza punto", SentenceSplitter.ExtractSentence("Prima. Ultima frase senza punto", 12));
    }

    [Fact]
    public void ExtractSentence_QuotesAndBracketsBelongToSentence()
    {
        var text = "Lui disse «Bravo!». Poi se ne andò (in fretta). Fine";
        Assert.Equal("Lui disse «Bravo!».", SentenceSplitter.ExtractSentence(text, 5));
        Assert.Equal("Poi se ne andò (in fretta).", SentenceSplitter.ExtractSentence(text, 25));
    }

    [Fact]
    public void ExtractSentence_EmojiSurvivesHereAndIsRemovedLater()
    {
        // lo spezzatore non tocca le emoji: se ne occupa EmojiFilter
        var s = SentenceSplitter.ExtractSentence("Ciao 😀 come stai? Bene.", 2);
        Assert.Equal("Ciao 😀 come stai?", s);
    }

    [Theory]
    [InlineData("Il dott. Bianchi e l'ing. Verdi sono arrivati. Poi via.", 2, "Il dott. Bianchi e l'ing. Verdi sono arrivati.")]
    [InlineData("Vedi pag. 12 del vol. 3. Poi continua.", 2, "Vedi pag. 12 del vol. 3.")]
    [InlineData("La S.p.A. ha sede a Roma. Bene.", 2, "La S.p.A. ha sede a Roma.")]
    [InlineData("G. Rossi è qui. Ciao.", 2, "G. Rossi è qui.")]
    [InlineData("Costa 3.5 euro. Ciao.", 2, "Costa 3.5 euro.")]
    [InlineData("Aspetta... non lo so. Davvero.", 2, "Aspetta... non lo so.")]
    [InlineData("Davvero?! Sì. No.", 2, "Davvero?!")]
    [InlineData("Ore 8.30: partenza. Ore 9: arrivo.", 2, "Ore 8.30: partenza.")]
    public void ExtractSentence_ItalianAbbreviationsAndNumbers(string text, int offset, string expected)
    {
        Assert.Equal(expected, SentenceSplitter.ExtractSentence(text, offset));
    }

    [Fact]
    public void SplitSentences_SplitsAndCleans()
    {
        var parts = SentenceSplitter.SplitSentences("Prima frase. Seconda frase!  Terza\r\nquarta.");
        Assert.Equal(new[] { "Prima frase.", "Seconda frase!", "Terza", "quarta." }, parts);
    }

    [Fact]
    public void SplitSentences_LongSentenceIsChunkedAtCommaOrSpace()
    {
        var longSentence = string.Join(", ", Enumerable.Range(1, 40).Select(i => $"elemento numero {i}")) + ".";
        var parts = SentenceSplitter.SplitSentences(longSentence, 120);
        Assert.True(parts.Count > 3);
        Assert.All(parts, p => Assert.True(p.Length <= 120, p));
        Assert.Equal(longSentence.Replace(", ", " ").Replace(" ", ""), string.Concat(parts).Replace(",", "").Replace(" ", ""));
    }

    [Fact]
    public void SplitSentences_EmptyInput()
    {
        Assert.Empty(SentenceSplitter.SplitSentences(""));
        Assert.Empty(SentenceSplitter.SplitSentences("   \r\n"));
    }
}
