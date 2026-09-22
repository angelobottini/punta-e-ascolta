namespace PuntaEAscolta.Logic.Text;

/// <summary>Spezzatura in frasi con regole italiane. Vedi docs/DESIGN.md sez. 3.1 e docs/research/probe-office.md.</summary>
public static class SentenceSplitter
{
    /// <summary>Restituisce la frase di <paramref name="paragraph"/> che contiene il carattere in posizione <paramref name="offset"/>, già ripulita dai caratteri di controllo.</summary>
    public static string ExtractSentence(string paragraph, int offset) => throw new NotImplementedException();

    /// <summary>Spezza un testo in frasi; le frasi più lunghe di <paramref name="maxChunkChars"/> vengono divise su virgola o spazio.</summary>
    public static IReadOnlyList<string> SplitSentences(string text, int maxChunkChars = 400) => throw new NotImplementedException();
}
