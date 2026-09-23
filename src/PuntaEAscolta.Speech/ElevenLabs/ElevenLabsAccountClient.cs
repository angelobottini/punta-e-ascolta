using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PuntaEAscolta.Speech.ElevenLabs;

/// <summary>
/// Chiamate di servizio all'account ElevenLabs, usate dalla finestra impostazioni: elenco voci, abbonamento e crediti,
/// verifica della chiave, elenco modelli. La chiave viene passata in chiaro dal chiamante (che l'ha appena decifrata o
/// l'ha appena digitata) e non viene mai registrata nei log né inclusa nei messaggi d'errore.
/// </summary>
public sealed class ElevenLabsAccountClient
{
    private const int VoicesPageSize = 100;
    private const int MaxVoicePages = 20;

    private readonly HttpClient _http;

    public ElevenLabsAccountClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Tutte le voci dell'account: GET /v2/voices con paginazione (page_size 100, next_page_token); se la v2 non risponde
    /// come previsto si ripiega su GET /v1/voices (senza paginazione). Ordinate per nome. Se alla chiave manca il permesso
    /// voices_read non si prova la v1 (servirebbe lo stesso permesso): SpeechProviderException con MissingPermission.
    /// </summary>
    public async Task<IReadOnlyList<ElevenLabsVoice>> GetVoicesAsync(string apiKey, CancellationToken ct)
    {
        RequireKey(apiKey);
        try
        {
            return await GetVoicesV2Async(apiKey, ct).ConfigureAwait(false);
        }
        catch (SpeechProviderException ex) when (ex.Reason is SpeechProviderReason.Configuration or SpeechProviderReason.Server)
        {
            return await GetVoicesV1Async(apiKey, ct).ConfigureAwait(false);
        }
    }

    public async Task<ElevenLabsSubscription> GetSubscriptionAsync(string apiKey, CancellationToken ct)
    {
        RequireKey(apiKey);
        var dto = await GetJsonAsync<SubscriptionDto>($"{ElevenLabsSynthesizer.BaseUrl}/v1/user/subscription", apiKey, ct).ConfigureAwait(false);
        return dto.ToModel();
    }

    /// <summary>
    /// Vero se la chiave è accettata (anche se le mancano dei permessi), falso se il server la rifiuta (401). Gli altri
    /// problemi (rete, server) vengono sollevati come SpeechProviderException, così la finestra può distinguere
    /// "chiave sbagliata" da "rete assente". Costruito su <see cref="CheckKeyAsync"/> senza la prova di lettura.
    /// </summary>
    public async Task<bool> ValidateKeyAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        var check = await CheckKeyAsync(apiKey, voiceId: null, ct).ConfigureAwait(false);
        return check.Valid;
    }

    /// <summary>Testo, modello e formato della prova di lettura: 5 caratteri, il modello più economico, il PCM più piccolo.</summary>
    internal const string ProbeText = "Prova";
    internal const string ProbeModel = "eleven_flash_v2_5";
    internal const string ProbeOutputFormat = "pcm_16000";

    /// <summary>
    /// Controllo completo della chiave, pensato per le chiavi con permessi limitati (ElevenLabs permette di concedere a una
    /// chiave solo alcune funzioni; una chiave "solo Text to Speech" riceve 401 missing_permissions su abbonamento e voci):
    /// <list type="number">
    /// <item>GET /v1/user/subscription: se manca user_read lo si annota e si prosegue; chiave rifiutata = non valida, fine.
    /// Rete, tempo o server: eccezione, come <see cref="ValidateKeyAsync"/>.</item>
    /// <item>GET /v2/voices (ripiego /v1/voices): se manca voices_read lo si annota e si prosegue, l'elenco resta null.</item>
    /// <item>Se c'è un ID di voce, una richiesta di sintesi minuscola ("Prova", modello Flash, pcm_16000) di cui si leggono
    /// i primi byte: conferma che la chiave sa davvero far leggere (permesso text_to_speech) e che la voce esiste.</item>
    /// </list>
    /// Il messaggio, in italiano, non contiene mai la chiave (i messaggi del server passano da Redact).
    /// </summary>
    public async Task<ElevenLabsKeyCheck> CheckKeyAsync(string apiKey, string? voiceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ElevenLabsKeyCheck(false, null, null, Array.Empty<string>(), null, "Nessuna chiave da verificare.");
        }

        var missing = new List<string>();
        var notes = new List<string>();

        // 1. Abbonamento: dice se la chiave è accettata. Rete e server: eccezione per il chiamante.
        ElevenLabsSubscription? subscription = null;
        try
        {
            subscription = await GetSubscriptionAsync(apiKey, ct).ConfigureAwait(false);
        }
        catch (SpeechProviderException ex) when (ex.Reason == SpeechProviderReason.InvalidKey)
        {
            return Rejected(ex);
        }
        catch (SpeechProviderException ex) when (ex.Reason == SpeechProviderReason.MissingPermission)
        {
            AddMissing(missing, ex.MissingPermission ?? ElevenLabsMessages.UserRead);
        }

        // 2. Voci.
        IReadOnlyList<ElevenLabsVoice>? voices = null;
        try
        {
            voices = await GetVoicesAsync(apiKey, ct).ConfigureAwait(false);
        }
        catch (SpeechProviderException ex) when (ex.Reason == SpeechProviderReason.InvalidKey)
        {
            return Rejected(ex);
        }
        catch (SpeechProviderException ex) when (ex.Reason == SpeechProviderReason.MissingPermission)
        {
            AddMissing(missing, ex.MissingPermission ?? ElevenLabsMessages.VoicesRead);
        }
        catch (SpeechProviderException ex)
        {
            notes.Add($"Elenco delle voci non disponibile: {ElevenLabsMessages.Explain(ex)}.");
        }

        // 3. Prova di lettura, solo se si sa quale voce usare.
        bool? ttsTested = null;
        SpeechProviderReason? ttsFailure = null;
        string? voice = string.IsNullOrWhiteSpace(voiceId) ? null : voiceId.Trim();
        if (voice is not null)
        {
            try
            {
                await ProbeTextToSpeechAsync(apiKey, voice, ct).ConfigureAwait(false);
                ttsTested = true;
            }
            catch (SpeechProviderException ex) when (ex.Reason == SpeechProviderReason.InvalidKey)
            {
                return Rejected(ex);
            }
            catch (SpeechProviderException ex)
            {
                ttsTested = false;
                ttsFailure = ex.Reason;
                if (ex.Reason == SpeechProviderReason.MissingPermission)
                    AddMissing(missing, ex.MissingPermission ?? ElevenLabsMessages.TextToSpeech);
                else
                    notes.Add($"Prova di lettura non riuscita: {ElevenLabsMessages.Explain(ex)}.");
            }
        }

        var sentences = new List<string>();
        sentences.AddRange(missing.Select(ElevenLabsMessages.DescribeMissingPermission));
        sentences.AddRange(notes);
        if (ttsTested == true) sentences.Add("Prova di lettura riuscita.");
        else if (ttsTested is null) sentences.Add(ElevenLabsMessages.SpeechNotTested);

        string message = sentences.Count == 0 ? "Chiave valida." : "Chiave valida. " + string.Join(" ", sentences);
        return new ElevenLabsKeyCheck(true, subscription, voices, missing, ttsTested, message)
        {
            TtsFailure = ttsFailure,
            Notes = sentences,
        };

        static ElevenLabsKeyCheck Rejected(SpeechProviderException ex) =>
            new(false, null, null, Array.Empty<string>(), null, ElevenLabsMessages.Explain(ex) + ".");

        static void AddMissing(List<string> list, string permission)
        {
            if (!list.Contains(permission, StringComparer.OrdinalIgnoreCase)) list.Add(permission);
        }
    }

    /// <summary>
    /// Sintesi minuscola (<see cref="ProbeText"/>) con la voce indicata: riesce se arriva almeno un byte di audio. Gli errori
    /// del server diventano SpeechProviderException (messaggio senza chiave).
    /// </summary>
    private async Task ProbeTextToSpeechAsync(string apiKey, string voiceId, CancellationToken ct)
    {
        string url = $"{ElevenLabsSynthesizer.BaseUrl}/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}/stream?output_format={ProbeOutputFormat}";
        var body = new TtsRequestBody { Text = ProbeText, ModelId = ProbeModel };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(body, ElevenLabsJson.Options), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ElevenLabsErrors.FromTransport(ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = ElevenLabsErrors.Redact(await ElevenLabsErrors.ReadAsync(response, ct).ConfigureAwait(false), apiKey);
                throw ElevenLabsErrors.ToException(error);
            }
            int read;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[512];
                read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ElevenLabsErrors.FromTransport(ex);
            }
            if (read <= 0)
            {
                throw new SpeechProviderException(SpeechProviderReason.Server, "ElevenLabs: nessun audio ricevuto nella prova di lettura.");
            }
        }
    }

    /// <summary>Modelli che fanno Text to Speech (GET /v1/models), con le lingue supportate.</summary>
    public async Task<IReadOnlyList<ElevenLabsModel>> GetModelsAsync(string apiKey, CancellationToken ct)
    {
        RequireKey(apiKey);
        var dtos = await GetJsonAsync<List<ModelDto>>($"{ElevenLabsSynthesizer.BaseUrl}/v1/models", apiKey, ct).ConfigureAwait(false);
        return dtos
            .Select(d => d.ToModel())
            .Where(m => m is { CanDoTextToSpeech: true })
            .Select(m => m!)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<ElevenLabsVoice>> GetVoicesV2Async(string apiKey, CancellationToken ct)
    {
        var voices = new List<ElevenLabsVoice>();
        string? token = null;
        for (int page = 0; page < MaxVoicePages; page++)
        {
            string url = $"{ElevenLabsSynthesizer.BaseUrl}/v2/voices?page_size={VoicesPageSize}"
                + (token is null ? "" : "&next_page_token=" + Uri.EscapeDataString(token));
            var dto = await GetJsonAsync<VoicesPageDto>(url, apiKey, ct).ConfigureAwait(false);
            if (dto.Voices is null)
            {
                throw new SpeechProviderException(SpeechProviderReason.Configuration, "ElevenLabs: risposta di /v2/voices senza elenco voci.");
            }
            voices.AddRange(dto.Voices.Select(v => v.ToModel()).Where(v => v is not null).Select(v => v!));
            token = dto.HasMore == true && !string.IsNullOrEmpty(dto.NextPageToken) ? dto.NextPageToken : null;
            if (token is null) break;
        }
        return Sort(voices);
    }

    private async Task<IReadOnlyList<ElevenLabsVoice>> GetVoicesV1Async(string apiKey, CancellationToken ct)
    {
        var dto = await GetJsonAsync<VoicesPageDto>($"{ElevenLabsSynthesizer.BaseUrl}/v1/voices", apiKey, ct).ConfigureAwait(false);
        var voices = (dto.Voices ?? []).Select(v => v.ToModel()).Where(v => v is not null).Select(v => v!).ToList();
        return Sort(voices);
    }

    private static IReadOnlyList<ElevenLabsVoice> Sort(List<ElevenLabsVoice> voices) =>
        voices
            .GroupBy(v => v.VoiceId)
            .Select(g => g.First())
            .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private async Task<T> GetJsonAsync<T>(string url, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ElevenLabsErrors.FromTransport(ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = ElevenLabsErrors.Redact(await ElevenLabsErrors.ReadAsync(response, ct).ConfigureAwait(false), apiKey);
                throw ElevenLabsErrors.ToException(error);
            }
            try
            {
                var value = await response.Content.ReadFromJsonAsync<T>(ElevenLabsJson.Options, ct).ConfigureAwait(false);
                return value ?? throw new SpeechProviderException(SpeechProviderReason.Configuration, $"ElevenLabs: risposta vuota da {Path(url)}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new SpeechProviderException(SpeechProviderReason.Configuration, $"ElevenLabs: risposta non interpretabile da {Path(url)}.", ex,
                    (int)HttpStatusCode.OK);
            }
            catch (Exception ex) when (ex is not SpeechProviderException)
            {
                throw ElevenLabsErrors.FromTransport(ex);
            }
        }
    }

    private static string Path(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;

    private static void RequireKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SpeechProviderException(SpeechProviderReason.NotConfigured, "ElevenLabs: chiave API non impostata.");
        }
    }
}
