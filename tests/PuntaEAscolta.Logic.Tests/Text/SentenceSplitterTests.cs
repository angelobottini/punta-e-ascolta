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

/// <summary>Correzioni della revisione del 22/09/2026: abbreviazioni che sono anche parole comuni.</summary>
public class SentenceSplitterReviewTests
{
    private const string WordParagraph = "La riunione del 3 ott. 2026 è stata spostata al pomeriggio, cioè alle 15.45 circa. Vedi pag. 12 e cfr. l'art. 5 del regolamento!\r";

    [Fact]
    public void Circa_FollowedByUppercase_EndsTheSentence()
    {
        int spostata = WordParagraph.IndexOf("spostata", StringComparison.Ordinal);
        int regolamento = WordParagraph.IndexOf("regolamento", StringComparison.Ordinal);
        Assert.Equal("La riunione del 3 ott. 2026 è stata spostata al pomeriggio, cioè alle 15.45 circa.", SentenceSplitter.ExtractSentence(WordParagraph, spostata));
        Assert.Equal("Vedi pag. 12 e cfr. l'art. 5 del regolamento!", SentenceSplitter.ExtractSentence(WordParagraph, regolamento));
    }

    [Theory]
    [InlineData("Ho detto di no. Poi sono uscito.", "Ho detto di no.")]
    [InlineData("Vado via. Torno domani.", "Vado via.")]
    [InlineData("Aspetta 5 min. Poi parti.", "Aspetta 5 min.")]
    [InlineData("Mele, pere ecc. Il resto domani.", "Mele, pere ecc.")]
    [InlineData("Ci vediamo il 10 gen. Porta i documenti.", "Ci vediamo il 10 gen.")]
    public void AmbiguousAbbreviation_BeforeUppercase_EndsTheSentence(string text, string expected)
    {
        Assert.Equal(expected, SentenceSplitter.ExtractSentence(text, 2));
    }

    [Theory]
    [InlineData("Il 3 mar. 2026 si parte per Roma.")]
    [InlineData("Costa 1.000 euro ecc. ma non importa.")]
    [InlineData("Il sig. Rossi e il dott. Bianchi sono qui.")]
    [InlineData("Vedi all. A del contratto.")]
    [InlineData("Documento n. 5 del reg. Lombardia.")]
    public void AbbreviationBeforeDigitLowercaseOrName_DoesNotEndTheSentence(string text)
    {
        Assert.Equal(text, SentenceSplitter.ExtractSentence(text, 2));
    }
}

/// <summary>Secondo giro della revisione: "gen." è sia gennaio sia generale.</summary>
public class SentenceSplitterMonthTests
{
    [Theory]
    [InlineData("Il gen. Rossi è arrivato. Poi è ripartito.", "Il gen. Rossi è arrivato.")]
    [InlineData("Il col. Bianchi è qui. Poi arriva il resto.", "Il col. Bianchi è qui.")]
    [InlineData("Ha parlato il gen. Dalla Chiesa. Tutti ascoltavano.", "Ha parlato il gen. Dalla Chiesa.")]
    [InlineData("Scrivi a mar. Rossi entro sera. Grazie.", "Scrivi a mar. Rossi entro sera.")]
    public void MonthAbbreviation_WithoutDayNumber_IsATitleAndNeverEnds(string text, string expected)
    {
        Assert.Equal(expected, SentenceSplitter.ExtractSentence(text, 2));
    }

    [Theory]
    [InlineData("Ci vediamo il 10 gen. Porta i documenti.", "Ci vediamo il 10 gen.")]
    [InlineData("Scadenza il 1° mag. Dopo non si accetta.", "Scadenza il 1° mag.")]
    [InlineData("Partenza il 31 dic. Ritorno a gennaio.", "Partenza il 31 dic.")]
    [InlineData("Arrivo il 5 sett. Poi si vedrà.", "Arrivo il 5 sett.")]
    public void MonthAbbreviation_AfterDayNumber_BeforeUppercase_EndsTheSentence(string text, string expected)
    {
        Assert.Equal(expected, SentenceSplitter.ExtractSentence(text, 2));
    }

    [Theory]
    [InlineData("Il 3 mar. 2026 si parte per Roma.")]
    [InlineData("Dal 10 gen. al 20 feb. si lavora.")]
    [InlineData("Nel 2026 gen. Rossi va in pensione.")]
    [InlineData("Nella stanza 15.30 gen. Verdi aspetta.")]
    public void MonthAbbreviation_BeforeDigitLowercaseOrWithoutDay_DoesNotEnd(string text)
    {
        Assert.Equal(text, SentenceSplitter.ExtractSentence(text, 2));
    }

    [Fact]
    public void SplitSentences_KeepsTitleButSplitsDate()
    {
        var parts = SentenceSplitter.SplitSentences("Il gen. Rossi è arrivato. Ci vediamo il 10 gen. Porta i documenti.");
        Assert.Equal(new[] { "Il gen. Rossi è arrivato.", "Ci vediamo il 10 gen.", "Porta i documenti." }, parts);
    }
}
