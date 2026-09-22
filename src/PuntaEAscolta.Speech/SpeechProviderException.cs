namespace PuntaEAscolta.Speech;

/// <summary>Motivo tipizzato di un fallimento del fornitore vocale in rete. Il servizio vocale decide il ripiego in base a questo.</summary>
public enum SpeechProviderReason
{
    /// <summary>Chiave API o voce non impostate: il fornitore non può essere usato.</summary>
    NotConfigured,
    /// <summary>Chiave API rifiutata (401): il cloud resta sospeso finché le impostazioni non cambiano.</summary>
    InvalidKey,
    /// <summary>Crediti esauriti (402, o 400/401 con codice storico quota_exceeded).</summary>
    QuotaExceeded,
    /// <summary>Troppe richieste o sistema occupato (429): nessun tentativo ripetuto, si ripiega solo per questa lettura.</summary>
    RateLimited,
    /// <summary>Errore di rete (DNS, connessione, flusso interrotto).</summary>
    Network,
    /// <summary>Tempo massimo superato prima del primo audio.</summary>
    Timeout,
    /// <summary>Errore del server (5xx).</summary>
    Server,
    /// <summary>Richiesta rifiutata per configurazione (400/403/404/422: voce non trovata, modello non valido, formato non ammesso...).</summary>
    Configuration
}

/// <summary>Eccezione tipizzata sollevata dai sintetizzatori in rete. Non contiene mai la chiave API.</summary>
public sealed class SpeechProviderException : Exception
{
    public SpeechProviderException(SpeechProviderReason reason, string message, Exception? innerException = null,
        int? httpStatus = null, string? errorCode = null)
        : base(message, innerException)
    {
        Reason = reason;
        HttpStatus = httpStatus;
        ErrorCode = errorCode;
    }

    public SpeechProviderReason Reason { get; }

    /// <summary>Codice HTTP della risposta, se il fallimento viene da una risposta del server.</summary>
    public int? HttpStatus { get; }

    /// <summary>Identificatore d'errore riportato dal server (es. invalid_api_key, quota_exceeded), se presente.</summary>
    public string? ErrorCode { get; }
}
