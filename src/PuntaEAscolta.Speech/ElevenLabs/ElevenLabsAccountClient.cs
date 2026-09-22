using System.Net;
using System.Net.Http.Json;
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
    /// come previsto si ripiega su GET /v1/voices (senza paginazione). Ordinate per nome.
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
    /// Vero se la chiave è accettata, falso se il server la rifiuta (401). Gli altri problemi (rete, server) vengono
    /// sollevati come SpeechProviderException, così la finestra può distinguere "chiave sbagliata" da "rete assente".
    /// </summary>
    public async Task<bool> ValidateKeyAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        try
        {
            await GetSubscriptionAsync(apiKey, ct).ConfigureAwait(false);
            return true;
        }
        catch (SpeechProviderException ex) when (ex.Reason == SpeechProviderReason.InvalidKey)
        {
            return false;
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
                var error = await ElevenLabsErrors.ReadAsync(response, ct).ConfigureAwait(false);
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
