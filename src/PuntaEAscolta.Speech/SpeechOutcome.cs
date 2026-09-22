using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Speech;

/// <summary>Come è finita una lettura.</summary>
public enum SpeechOutcome
{
    /// <summary>Letta fino in fondo (eventuali pezzi non sintetizzabili sono stati saltati).</summary>
    Completed,

    /// <summary>Fermata da Stop() (clic, Esc, scorciatoia) o dalla chiusura del servizio.</summary>
    Stopped,

    /// <summary>Interrotta da una nuova lettura.</summary>
    Superseded,

    /// <summary>Nessun pezzo è stato pronunciato: anche la voce locale ha fallito.</summary>
    Failed
}

/// <summary>
/// Estensione di ISpeechService che dice come è finita la lettura. La usa la dettatura: una rilettura fermata
/// dall'utente annulla l'inserimento, una rilettura interrotta da un'altra lettura lo annulla senza parlarci sopra.
/// Proposta di contratto per PuntaEAscolta.Core: vedi docs/impl-notes/speech.md.
/// </summary>
public interface ISpeechServiceWithOutcome : ISpeechService
{
    /// <summary>Come SpeakAsync, ma restituisce l'esito. Solleva OperationCanceledException solo se viene annullato <paramref name="ct"/>.</summary>
    Task<SpeechOutcome> SpeakWithOutcomeAsync(SpeechRequest request, CancellationToken ct);
}
