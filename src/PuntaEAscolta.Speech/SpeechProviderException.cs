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
    Configuration,
    /// <summary>
    /// La chiave è valida ma non ha il permesso per questa chiamata (codice missing_permissions: le chiavi ElevenLabs si
    /// possono limitare per funzione). Il permesso mancante, se il server lo nomina, è in
    /// <see cref="SpeechProviderException.MissingPermission"/>. Per la sintesi vale come una chiave rifiutata: il cloud resta
    /// sospeso finché le impostazioni non cambiano.
    /// </summary>
    MissingPermission
}

/// <summary>Eccezione tipizzata sollevata dai sintetizzatori in rete. Non contiene mai la chiave API.</summary>
public sealed class SpeechProviderException : Exception
{
    public SpeechProviderException(SpeechProviderReason reason, string message, Exception? innerException = null,
        int? httpStatus = null, string? errorCode = null, string? missingPermission = null, string? serverMessage = null)
        : base(message, innerException)
    {
        Reason = reason;
        HttpStatus = httpStatus;
        ErrorCode = errorCode;
        MissingPermission = missingPermission;
        ServerMessage = serverMessage;
    }

    public SpeechProviderReason Reason { get; }

    /// <summary>Codice HTTP della risposta, se il fallimento viene da una risposta del server.</summary>
    public int? HttpStatus { get; }

    /// <summary>Identificatore d'errore riportato dal server (es. invalid_api_key, quota_exceeded), se presente.</summary>
    public string? ErrorCode { get; }

    /// <summary>Permesso che manca alla chiave (es. user_read, voices_read, text_to_speech), se il server lo nomina.</summary>
    public string? MissingPermission { get; }

    /// <summary>Messaggio del server, già senza la chiave e troncato; null se la risposta non ne aveva.</summary>
    public string? ServerMessage { get; }
}
