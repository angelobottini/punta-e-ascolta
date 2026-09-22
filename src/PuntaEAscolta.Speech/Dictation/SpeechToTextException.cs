namespace PuntaEAscolta.Speech.Dictation;

/// <summary>
/// Motivo del fallimento di una trascrizione. Le categorie ricalcano quelle del sintetizzatore ElevenLabs
/// (InvalidKey, QuotaExceeded, RateLimited, Network, Timeout, Server, NotConfigured) con due aggiunte
/// specifiche della dettatura: Forbidden (chiave senza permesso Speech to Text) e InvalidRequest.
/// </summary>
public enum SpeechToTextFailure
{
    /// <summary>Chiave API assente nelle impostazioni oppure non decifrabile su questo PC.</summary>
    NotConfigured,

    /// <summary>Chiave rifiutata dal server (HTTP 401).</summary>
    InvalidKey,

    /// <summary>Chiave accettata ma senza permesso per la trascrizione, o funzione non inclusa nel piano (HTTP 403).</summary>
    Forbidden,

    /// <summary>Crediti esauriti (HTTP 402, oppure codice quota_exceeded / insufficient_credits con qualunque HTTP).</summary>
    QuotaExceeded,

    /// <summary>Troppe richieste o servizio occupato (HTTP 429).</summary>
    RateLimited,

    /// <summary>Richiesta rifiutata dal server (HTTP 400, 404, 422): parametri o audio non accettati.</summary>
    InvalidRequest,

    /// <summary>Rete non disponibile o connessione fallita.</summary>
    Network,

    /// <summary>Il server non ha risposto entro il tempo concesso dal chiamante.</summary>
    Timeout,

    /// <summary>Errore del server (HTTP 5xx) o risposta non interpretabile.</summary>
    Server
}

/// <summary>Errore tipizzato del servizio di trascrizione. Il messaggio non contiene mai la chiave API.</summary>
public sealed class SpeechToTextException : Exception
{
    public SpeechToTextException(SpeechToTextFailure reason, string message, Exception? innerException = null,
        int statusCode = 0, string? code = null)
        : base(message, innerException)
    {
        Reason = reason;
        StatusCode = statusCode;
        Code = code;
    }

    public SpeechToTextFailure Reason { get; }

    /// <summary>Codice HTTP della risposta, 0 se l'errore non viene dal server.</summary>
    public int StatusCode { get; }

    /// <summary>Identificatore dell'errore restituito dal server (detail.code o detail.status), se presente.</summary>
    public string? Code { get; }
}
