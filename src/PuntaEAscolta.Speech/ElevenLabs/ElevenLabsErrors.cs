using System.Net;
using System.Text.Json;

namespace PuntaEAscolta.Speech.ElevenLabs;

/// <summary>
/// Lettura tollerante degli errori ElevenLabs e mappatura sui motivi tipizzati.
/// Il corpo può avere tre forme: {detail:{code|status, message, request_id}} (nuova e storica),
/// {detail:[{loc, msg, type}]} (422) oppure {detail:"testo"}. L'identificatore (code o status) conta più del codice HTTP,
/// perché le due famiglie di nomi (invalid_api_key/quota_exceeded/too_many_concurrent_requests e
/// insufficient_credits/concurrent_limit_exceeded/rate_limit_exceeded) convivono nelle fonti ufficiali.
/// </summary>
internal static class ElevenLabsErrors
{
    private const int MaxBodyChars = 2000;

    public static async Task<ElevenLabsError> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Corpo non leggibile: basta il codice HTTP.
        }
        return Parse((int)response.StatusCode, body);
    }

    public static ElevenLabsError Parse(int status, string body)
    {
        string? code = null, message = null, requestId = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var detail))
                {
                    switch (detail.ValueKind)
                    {
                        case JsonValueKind.Object:
                            code = Str(detail, "code") ?? Str(detail, "status") ?? Str(detail, "type");
                            message = Str(detail, "message") ?? Str(detail, "msg");
                            requestId = Str(detail, "request_id");
                            break;
                        case JsonValueKind.Array when detail.GetArrayLength() > 0:
                            code = "validation_error";
                            message = string.Join("; ", detail.EnumerateArray()
                                .Select(e => Str(e, "msg") ?? Str(e, "message"))
                                .Where(m => !string.IsNullOrEmpty(m)));
                            break;
                        case JsonValueKind.String:
                            message = detail.GetString();
                            break;
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    code = Str(root, "code") ?? Str(root, "status") ?? Str(root, "error");
                    message = Str(root, "message");
                }
            }
            catch (JsonException)
            {
                // Corpo intero: si tronca solo DOPO aver tolto la chiave (Redact), altrimenti il taglio può spezzarla
                // e lasciarne una parte nel log.
                message = body;
            }
        }
        if (string.IsNullOrWhiteSpace(message)) message = null;
        return new ElevenLabsError(status, string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToLowerInvariant(), message, requestId);

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
    }

    /// <summary>
    /// Toglie un segreto (la chiave API) dal messaggio d'errore del server: un proxy o un server che rimanda le intestazioni
    /// nel corpo non deve farla arrivare nei log attraverso il messaggio dell'eccezione. Poi tronca il messaggio a 2000
    /// caratteri: sempre in quest'ordine, perché un taglio fatto prima può spezzare la chiave e lasciarne un pezzo.
    /// </summary>
    public static ElevenLabsError Redact(ElevenLabsError error, string? secret)
    {
        string? message = error.Message;
        string? code = error.Code;
        if (!string.IsNullOrEmpty(secret))
        {
            if (message?.Contains(secret, StringComparison.Ordinal) == true) message = message.Replace(secret, "***", StringComparison.Ordinal);
            if (code?.Contains(secret, StringComparison.OrdinalIgnoreCase) == true) code = "***";
        }
        if (message is not null && message.Length > MaxBodyChars) message = message[..MaxBodyChars];
        if (ReferenceEquals(message, error.Message) && ReferenceEquals(code, error.Code)) return error;
        return error with { Message = message, Code = code };
    }

    public static SpeechProviderReason Map(ElevenLabsError error)
    {
        // Chiave valida ma senza il permesso per questa chiamata (le chiavi si limitano per funzione): prima del 401 generico,
        // che altrimenti la farebbe sembrare sbagliata (prove dal vivo del 23/09/2026: "missing the permission user_read").
        if (IsMissingPermission(error)) return SpeechProviderReason.MissingPermission;

        switch (error.Code)
        {
            case "invalid_api_key":
            case "missing_api_key":
            case "invalid_authorization_header":
            case "unauthorized":
            case "authentication_error":
                return SpeechProviderReason.InvalidKey;
            case "quota_exceeded":
            case "insufficient_credits":
            case "payment_required":
                return SpeechProviderReason.QuotaExceeded;
            case "too_many_concurrent_requests":
            case "concurrent_limit_exceeded":
            case "rate_limit_exceeded":
            case "system_busy":
            case "rate_limit_error":
                return SpeechProviderReason.RateLimited;
        }

        return error.Status switch
        {
            (int)HttpStatusCode.Unauthorized => SpeechProviderReason.InvalidKey,
            (int)HttpStatusCode.PaymentRequired => SpeechProviderReason.QuotaExceeded,
            (int)HttpStatusCode.TooManyRequests => SpeechProviderReason.RateLimited,
            >= 500 => SpeechProviderReason.Server,
            _ => SpeechProviderReason.Configuration
        };
    }

    /// <summary>
    /// Vero se il server dice che alla chiave manca un permesso: codice (o status) missing_permissions, oppure un 401 o 403
    /// il cui messaggio dice "missing the permission" o contiene missing_permissions.
    /// </summary>
    public static bool IsMissingPermission(ElevenLabsError error)
    {
        if (error.Code is "missing_permissions" or "missing_permission") return true;
        if (error.Status is not (401 or 403)) return false;
        string m = error.Message ?? "";
        return m.Contains("missing the permission", StringComparison.OrdinalIgnoreCase)
            || m.Contains("missing_permission", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Nome del permesso mancante nel messaggio del server ("... is missing the permission user_read to execute this
    /// operation."), in minuscolo; null se il messaggio non lo nomina. I permessi di ElevenLabs sono nomi con il trattino
    /// basso (user_read, voices_read, text_to_speech): una parola semplice dopo "permission" ("to", "for") non è un permesso.
    /// </summary>
    public static string? ParseMissingPermission(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        try
        {
            var match = s_permissionPattern.Match(message);
            return match.Success ? match.Groups["name"].Value.ToLowerInvariant() : null;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex s_permissionPattern = new(
        @"missing(?:\s+the)?[\s_]+permissions?\s*:?\s*[""'`]?(?<name>[a-z][a-z0-9]*(?:_[a-z0-9]+)+)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Vero quando il server rifiuta il formato di uscita (pcm_44100 richiede il piano Pro): codice invalid_output_format,
    /// oppure qualunque 4xx non di autenticazione, permessi, quota o concorrenza che nomina il formato.
    /// </summary>
    public static bool IsOutputFormatRejection(ElevenLabsError error)
    {
        if (error.Status is < 400 or >= 500) return false;
        var reason = Map(error);
        if (reason is SpeechProviderReason.InvalidKey or SpeechProviderReason.QuotaExceeded or SpeechProviderReason.RateLimited
            or SpeechProviderReason.MissingPermission) return false;
        if (error.Code is not null && error.Code.Contains("format", StringComparison.OrdinalIgnoreCase)) return true;
        string m = error.Message ?? "";
        return m.Contains("output_format", StringComparison.OrdinalIgnoreCase)
            || m.Contains("output format", StringComparison.OrdinalIgnoreCase)
            || m.Contains("pcm", StringComparison.OrdinalIgnoreCase)
            || m.Contains("44.1", StringComparison.OrdinalIgnoreCase)
            || m.Contains("44100", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Eccezione tipizzata per una risposta d'errore. <paramref name="error"/> deve essere già passato da <see cref="Redact"/>.</summary>
    public static SpeechProviderException ToException(ElevenLabsError error)
    {
        var reason = Map(error);
        string? permission = reason == SpeechProviderReason.MissingPermission ? ParseMissingPermission(error.Message) : null;
        string what = reason switch
        {
            SpeechProviderReason.InvalidKey => "chiave API rifiutata",
            SpeechProviderReason.MissingPermission => permission is null ? "la chiave non ha il permesso per questa operazione" : $"la chiave non ha il permesso {permission}",
            SpeechProviderReason.QuotaExceeded => "crediti esauriti",
            SpeechProviderReason.RateLimited => "troppe richieste in corso",
            SpeechProviderReason.Server => "errore del server",
            _ => "richiesta rifiutata"
        };
        string detail = error.Code is null ? "" : $" {error.Code}";
        string message = error.Message is null ? "" : $": {error.Message}";
        return new SpeechProviderException(reason, $"ElevenLabs: {what} (HTTP {error.Status}{detail}){message}",
            httpStatus: error.Status, errorCode: error.Code, missingPermission: permission, serverMessage: error.Message);
    }

    /// <summary>
    /// Traduce le eccezioni di trasporto di HttpClient. Chi chiama deve aver già escluso l'annullamento richiesto dal proprio token:
    /// qui una OperationCanceledException significa timeout interno di HttpClient.
    /// </summary>
    public static SpeechProviderException FromTransport(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return new SpeechProviderException(SpeechProviderReason.Timeout, "ElevenLabs: tempo massimo di risposta superato.", ex);
        }
        if (ex is HttpRequestException http)
        {
            return new SpeechProviderException(SpeechProviderReason.Network, $"ElevenLabs: errore di rete ({http.Message}).", ex,
                http.StatusCode is null ? null : (int)http.StatusCode.Value);
        }
        if (ex is IOException or System.Net.Sockets.SocketException)
        {
            return new SpeechProviderException(SpeechProviderReason.Network, $"ElevenLabs: errore di rete ({ex.Message}).", ex);
        }
        return new SpeechProviderException(SpeechProviderReason.Network, $"ElevenLabs: errore imprevisto ({ex.GetType().Name}: {ex.Message}).", ex);
    }
}
