using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Speech.Dictation;

/// <summary>
/// Trascrizione batch con ElevenLabs Scribe: <c>POST /v1/speech-to-text</c> in multipart/form-data con il PCM grezzo
/// (16 kHz, mono, 16 bit) e <c>file_format=pcm_s16le_16</c>, come confermato dalla OpenAPI in docs/research/dictation.md sez. 6.
/// Ripieghi automatici, ciascuno al massimo una volta per chiamata: modello sconosciuto (400/422) → <c>scribe_v1</c>;
/// PCM grezzo rifiutato (400/422) → stesso audio incapsulato in un WAV in memoria.
/// La chiave API viene decifrata a ogni chiamata, inviata solo nell'intestazione <c>xi-api-key</c> e non finisce mai nei log.
/// </summary>
public sealed partial class ElevenLabsSpeechToText : ISpeechToText
{
    internal const string Endpoint = "https://api.elevenlabs.io/v1/speech-to-text";
    internal const string DefaultModel = "scribe_v2";
    internal const string FallbackModel = "scribe_v1";
    internal const string RawPcmFormat = "pcm_s16le_16";
    internal const string RawPcmFileName = "audio.pcm";
    internal const string WavFileName = "audio.wav";

    /// <summary>Il server rifiuta clip sotto i 100 ms: 16000 Hz x 2 byte x 0,1 s.</summary>
    internal const int MinPcmBytes = 3200;

    private const int SampleRate = 16000;
    private const int BytesPerSecond = SampleRate * 2;
    private const int MaxLoggedMessageChars = 200;

    private readonly HttpClient _http;
    private readonly ISettingsStore _settings;
    private readonly ISecretProtector _protector;
    private readonly ILog _log;
    private readonly object _gate = new();

    /// <summary>Modello rifiutato dal server in questa sessione: le chiamate successive passano subito al ripiego.</summary>
    private string? _rejectedModel;

    /// <summary>Il server ha rifiutato il PCM grezzo in questa sessione: si invia direttamente il WAV.</summary>
    private bool _sendWav;

    public ElevenLabsSpeechToText(HttpClient http, ISettingsStore settings, ISecretProtector protector, ILog log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>True se nelle impostazioni c'è una chiave ElevenLabs decifrabile su questo PC.</summary>
    public bool IsConfigured => TryGetApiKey(out _);

    public async Task<string> TranscribeAsync(byte[] pcm16kMono, string languageCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcm16kMono);
        ct.ThrowIfCancellationRequested();

        if (!TryGetApiKey(out var apiKey))
            throw new SpeechToTextException(SpeechToTextFailure.NotConfigured, "Chiave API ElevenLabs assente o non decifrabile.");

        if (pcm16kMono.Length < MinPcmBytes)
        {
            _log.Debug($"Trascrizione: clip di {pcm16kMono.Length} byte sotto i 100 ms, non inviata.");
            return "";
        }

        string configuredModel = _settings.Current.Dictation.Model;
        if (string.IsNullOrWhiteSpace(configuredModel)) configuredModel = DefaultModel;
        configuredModel = configuredModel.Trim();

        string model;
        bool sendWav;
        lock (_gate)
        {
            model = configuredModel == _rejectedModel ? FallbackModel : configuredModel;
            sendWav = _sendWav;
        }

        bool modelRetried = false, formatRetried = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            using var request = BuildRequest(apiKey, pcm16kMono, model, languageCode, sendWav);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _log.Warn($"Trascrizione: rete non disponibile ({ex.GetType().Name}: {Truncate(ex.Message)}).");
                throw new SpeechToTextException(SpeechToTextFailure.Network, "Rete non disponibile o server ElevenLabs non raggiungibile.", ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _log.Warn("Trascrizione: tempo massimo della richiesta HTTP superato.");
                throw new SpeechToTextException(SpeechToTextFailure.Timeout, "Il server ElevenLabs non ha risposto in tempo.", ex);
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    string text = ParseText(body);
                    _log.Info($"Trascrizione: {pcm16kMono.Length / (double)BytesPerSecond:F1} s di audio, modello {model}, " +
                              $"{(sendWav ? "WAV" : "PCM")}, HTTP {status} in {stopwatch.ElapsedMilliseconds} ms, {text.Length} caratteri.");
                    if (_log.IsDebugEnabled) _log.Debug("Trascrizione: testo riconosciuto: " + text);
                    return text;
                }

                var error = await ReadErrorAsync(response, ct).ConfigureAwait(false);
                _log.Warn($"Trascrizione: Scribe ha risposto HTTP {status}" +
                          (error.Code is null ? "" : $" ({error.Code})") +
                          (error.Message is null ? "" : $": {Truncate(error.Message)}") + ".");

                bool validationError = status is 400 or 422;
                if (validationError && !modelRetried && model != FallbackModel && error.Mentions("model"))
                {
                    _log.Warn($"Trascrizione: modello {model} non accettato, riprovo una volta con {FallbackModel}.");
                    lock (_gate) _rejectedModel = model;
                    model = FallbackModel;
                    modelRetried = true;
                    continue;
                }

                if (validationError && !formatRetried && !sendWav &&
                    (error.Mentions("format") || error.Mentions("audio") || error.Mentions("file")))
                {
                    _log.Warn("Trascrizione: PCM grezzo non accettato, riprovo una volta con un WAV.");
                    lock (_gate) _sendWav = true;
                    sendWav = true;
                    formatRetried = true;
                    continue;
                }

                throw error.ToException();
            }
        }
    }

    private bool TryGetApiKey(out string apiKey)
    {
        apiKey = "";
        string protectedKey = _settings.Current.Speech.ElevenLabsProtectedApiKey;
        if (string.IsNullOrWhiteSpace(protectedKey)) return false;
        try
        {
            string? plain = _protector.Unprotect(protectedKey);
            if (string.IsNullOrWhiteSpace(plain)) return false;
            apiKey = plain.Trim();
            return true;
        }
        catch (Exception ex)
        {
            // Solo il tipo dell'eccezione: il messaggio potrebbe contenere dati protetti.
            _log.Warn($"Trascrizione: chiave ElevenLabs non decifrabile ({ex.GetType().Name}).");
            return false;
        }
    }

    private static HttpRequestMessage BuildRequest(string apiKey, byte[] pcm, string model, string languageCode, bool sendWav)
    {
        var form = new MultipartFormDataContent();
        AddField(form, "model_id", model);
        if (!string.IsNullOrWhiteSpace(languageCode)) AddField(form, "language_code", languageCode.Trim());
        AddField(form, "tag_audio_events", "false");
        AddField(form, "diarize", "false");

        if (sendWav)
        {
            var file = new ByteArrayContent(WavWriter.Wrap(pcm, SampleRate, channels: 1, bitsPerSample: 16));
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", WavFileName);
        }
        else
        {
            AddField(form, "file_format", RawPcmFormat);
            var file = new ByteArrayContent(pcm);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", RawPcmFileName);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void AddField(MultipartFormDataContent form, string name, string value)
    {
        var content = new StringContent(value, Encoding.UTF8);
        content.Headers.ContentType = null; // i campi di testo di un form non portano Content-Type
        form.Add(content, name);
    }

    /// <summary>Estrae <c>text</c> dalla risposta e normalizza gli spazi (anche i ritorni a capo: l'a capo si detta come comando).</summary>
    private static string ParseText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            if (!doc.RootElement.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String) return "";
            string raw = textElement.GetString() ?? "";
            return WhitespaceRun().Replace(raw, " ").Trim();
        }
        catch (JsonException ex)
        {
            throw new SpeechToTextException(SpeechToTextFailure.Server, "Risposta di ElevenLabs non interpretabile.", ex);
        }
    }

    /// <summary>Legge il corpo d'errore tollerando le forme note: detail oggetto (code/status, message), detail array (422), detail stringa.</summary>
    private static async Task<ScribeError> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        int status = (int)response.StatusCode;
        string body;
        try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new ScribeError(status, null, null); }
        if (string.IsNullOrWhiteSpace(body)) return new ScribeError(status, null, null);

        string? code = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("detail", out var detail))
            {
                switch (detail.ValueKind)
                {
                    case JsonValueKind.Object:
                        code = Str(detail, "code") ?? Str(detail, "status");
                        message = Str(detail, "message");
                        break;
                    case JsonValueKind.Array when detail.GetArrayLength() > 0:
                        code = "validation_error";
                        message = string.Join("; ", detail.EnumerateArray().Select(e => Str(e, "msg")).Where(m => m is not null));
                        if (message.Length == 0) message = null;
                        break;
                    case JsonValueKind.String:
                        message = detail.GetString();
                        break;
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                message = Str(doc.RootElement, "message") ?? Str(doc.RootElement, "error");
            }
        }
        catch (JsonException)
        {
            message = Truncate(body);
        }
        return new ScribeError(status, code, message);

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
    }

    private static string Truncate(string text) =>
        text.Length > MaxLoggedMessageChars ? text[..MaxLoggedMessageChars] + "…" : text;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    private sealed record ScribeError(int Status, string? Code, string? Message)
    {
        public bool Mentions(string word) =>
            (Code?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (Message?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false);

        public SpeechToTextException ToException()
        {
            var reason = MapReason(Status, Code);
            string text = $"ElevenLabs Scribe ha risposto HTTP {Status}" +
                          (Code is null ? "" : $" ({Code})") +
                          (Message is null ? "" : $": {Truncate(Message)}") + ".";
            return new SpeechToTextException(reason, text, null, Status, Code);
        }

        /// <summary>L'identificatore vince sul codice HTTP: la quota è stata segnalata storicamente anche con 400 e 401.</summary>
        internal static SpeechToTextFailure MapReason(int status, string? code)
        {
            switch (code)
            {
                case "quota_exceeded":
                case "insufficient_credits":
                    return SpeechToTextFailure.QuotaExceeded;
                case "too_many_concurrent_requests":
                case "concurrent_limit_exceeded":
                case "rate_limit_exceeded":
                case "system_busy":
                    return SpeechToTextFailure.RateLimited;
            }
            return status switch
            {
                401 => SpeechToTextFailure.InvalidKey,
                402 => SpeechToTextFailure.QuotaExceeded,
                403 => SpeechToTextFailure.Forbidden,
                429 => SpeechToTextFailure.RateLimited,
                400 or 404 or 422 => SpeechToTextFailure.InvalidRequest,
                _ => SpeechToTextFailure.Server
            };
        }
    }
}

/// <summary>Incapsula PCM lineare in un file WAV (intestazione RIFF canonica di 44 byte) in memoria.</summary>
internal static class WavWriter
{
    public const int HeaderBytes = 44;

    public static byte[] Wrap(byte[] pcm, int sampleRate, short channels, short bitsPerSample)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        int blockAlign = channels * bitsPerSample / 8;
        var wav = new byte[HeaderBytes + pcm.Length];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(span[8..]);

        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);                       // dimensione del blocco fmt
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);                        // PCM lineare
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * blockAlign);  // byte al secondo
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], bitsPerSample);

        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Length);
        pcm.CopyTo(span[HeaderBytes..]);
        return wav;
    }
}
