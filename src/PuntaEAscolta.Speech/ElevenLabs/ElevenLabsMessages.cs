namespace PuntaEAscolta.Speech.ElevenLabs;

/// <summary>
/// Spiegazioni in italiano semplice dei problemi con ElevenLabs, per la finestra impostazioni e la riga di comando
/// (l'assistente deve capire che cosa fare). Il messaggio del server, già senza chiave, si aggiunge fra parentesi.
/// </summary>
public static class ElevenLabsMessages
{
    public const string UserRead = "user_read";
    public const string VoicesRead = "voices_read";
    public const string TextToSpeech = "text_to_speech";

    /// <summary>Frase completa per un permesso che manca alla chiave, es. "Non ha il permesso di elencare le voci (voices_read): ...".</summary>
    public static string DescribeMissingPermission(string permission)
    {
        string p = (permission ?? "").Trim().ToLowerInvariant();
        return p switch
        {
            UserRead => "Non ha il permesso di leggere i dati dell'abbonamento (user_read): i crediti non si possono mostrare.",
            VoicesRead => "Non ha il permesso di elencare le voci (voices_read): l'ID della voce va scritto a mano.",
            TextToSpeech => "Non ha il permesso di sintesi vocale (text_to_speech): con questa chiave ElevenLabs non può leggere. " +
                            "Sul sito di ElevenLabs, nelle impostazioni della chiave, attivare \"Text to Speech\".",
            "models_read" => "Non ha il permesso di elencare i modelli (models_read): si usano i modelli scritti nelle impostazioni.",
            "speech_to_text" => "Non ha il permesso di trascrizione (speech_to_text): la dettatura non funziona con questa chiave.",
            "" => "Non ha il permesso per una delle operazioni.",
            _ => $"Non ha il permesso {p}.",
        };
    }

    /// <summary>Spiegazione breve di un fallimento, con il messaggio del server fra parentesi se c'è.</summary>
    public static string Explain(SpeechProviderException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string what = ex.Reason switch
        {
            SpeechProviderReason.InvalidKey => "ElevenLabs ha rifiutato la chiave: controllare di averla copiata per intero",
            SpeechProviderReason.MissingPermission => ex.MissingPermission is { } p
                ? TrimFinalDot(DescribeMissingPermission(p)).Replace("Non ha il permesso", "la chiave non ha il permesso", StringComparison.Ordinal)
                : "la chiave non ha il permesso per questa operazione (vedere i permessi della chiave sul sito di ElevenLabs)",
            SpeechProviderReason.QuotaExceeded => "crediti ElevenLabs esauriti",
            SpeechProviderReason.RateLimited => "troppe richieste in corso a ElevenLabs: riprovare fra poco",
            SpeechProviderReason.Network => "rete non disponibile o ElevenLabs non raggiungibile",
            SpeechProviderReason.Timeout => "ElevenLabs non ha risposto in tempo",
            SpeechProviderReason.Server => "errore del server di ElevenLabs: riprovare più tardi",
            SpeechProviderReason.NotConfigured => "servono la chiave e l'ID della voce",
            _ when IsVoiceNotFound(ex) => "la voce con questo ID non esiste o non è nell'account: controllare l'ID della voce",
            _ when IsModelProblem(ex) => "il modello scelto non è valido per questo account",
            _ => "ElevenLabs ha rifiutato la richiesta",
        };
        return ex.ServerMessage is { Length: > 0 } server ? $"{what} (risposta di ElevenLabs: {server})" : what;
    }

    private static bool IsVoiceNotFound(SpeechProviderException ex)
    {
        string code = ex.ErrorCode ?? "";
        string server = ex.ServerMessage ?? "";
        return code.Contains("voice", StringComparison.OrdinalIgnoreCase)
            || (ex.HttpStatus == 404 && !code.Contains("model", StringComparison.OrdinalIgnoreCase))
            || server.Contains("voice_id", StringComparison.OrdinalIgnoreCase)
            || server.Contains("voice not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModelProblem(SpeechProviderException ex) =>
        (ex.ErrorCode ?? "").Contains("model", StringComparison.OrdinalIgnoreCase);

    private static string TrimFinalDot(string text) => text.EndsWith('.') ? text[..^1] : text;
}
