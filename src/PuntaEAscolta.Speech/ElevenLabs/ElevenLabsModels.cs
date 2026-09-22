using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Speech.ElevenLabs;

// ---------------------------------------------------------------------------------------------------------------------
// Modelli pubblici (usati dalla finestra impostazioni e dal servizio vocale)
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Voce presente nell'account ElevenLabs (GET /v2/voices, ripiego GET /v1/voices).</summary>
public sealed record ElevenLabsVoice(
    string VoiceId,
    string Name,
    string? Category,
    IReadOnlyDictionary<string, string> Labels,
    string? PreviewUrl)
{
    public string? Gender => Labels.GetValueOrDefault("gender");
    public string? Accent => Labels.GetValueOrDefault("accent");
    public string? Language => Labels.GetValueOrDefault("language");
    public string? UseCase => Labels.GetValueOrDefault("use_case");

    /// <summary>Descrizione breve per l'elenco nella finestra impostazioni, es. "Tiziana (female, italian, educational)".</summary>
    public string DisplayName
    {
        get
        {
            var parts = new[] { Gender, Accent ?? Language, UseCase }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            return parts.Length == 0 ? Name : $"{Name} ({string.Join(", ", parts)})";
        }
    }
}

/// <summary>Stato dell'abbonamento (GET /v1/user/subscription). I crediti residui sono CharacterLimit - CharacterCount.</summary>
public sealed record ElevenLabsSubscription(
    string Tier,
    string Status,
    long CharacterCount,
    long CharacterLimit,
    long? NextCharacterCountResetUnix)
{
    public long CharactersRemaining => Math.Max(0, CharacterLimit - CharacterCount);

    public DateTimeOffset? NextReset =>
        NextCharacterCountResetUnix is > 0 ? DateTimeOffset.FromUnixTimeSeconds(NextCharacterCountResetUnix.Value) : null;
}

/// <summary>Modello di sintesi disponibile (GET /v1/models), già filtrato su quelli che fanno Text to Speech.</summary>
public sealed record ElevenLabsModel(
    string ModelId,
    string Name,
    bool CanDoTextToSpeech,
    IReadOnlyList<string> Languages,
    int? MaxCharactersPerRequest,
    double? CharacterCostMultiplier)
{
    public bool SupportsItalian => Languages.Any(l => l.Equals("it", StringComparison.OrdinalIgnoreCase));
    public bool SupportsLanguageCode => ElevenLabsPlan.ModelSupportsLanguageCode(ModelId);
}

/// <summary>Parametri della voce inviati in voice_settings. Valori già limitati agli intervalli ammessi.</summary>
public sealed record ElevenLabsVoiceSettings(
    double Stability = 0.5,
    double SimilarityBoost = 0.75,
    double Style = 0.0,
    bool UseSpeakerBoost = true,
    double Speed = 1.0)
{
    public static ElevenLabsVoiceSettings FromSettings(SpeechSettings s) => new(
        Clamp01(s.ElevenLabsStability, 0.5),
        Clamp01(s.ElevenLabsSimilarity, 0.75),
        Clamp01(s.ElevenLabsStyle, 0.0),
        true,
        Clamp(s.ElevenLabsSpeed, 0.7, 1.2, 1.0));

    /// <summary>Rappresentazione stabile (2 decimali, cultura invariante) per la chiave di cache.</summary>
    public string ToCacheString() => string.Join('|',
        Stability.ToString("F2", CultureInfo.InvariantCulture),
        SimilarityBoost.ToString("F2", CultureInfo.InvariantCulture),
        Style.ToString("F2", CultureInfo.InvariantCulture),
        Speed.ToString("F2", CultureInfo.InvariantCulture),
        UseSpeakerBoost ? "1" : "0");

    private static double Clamp01(double value, double fallback) => Clamp(value, 0.0, 1.0, fallback);

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>
/// Piano di una richiesta: voce, modello, lingua, formato e parametri, calcolati dalle impostazioni e dal tipo di testo.
/// Etichette (Label) → modello delle etichette con language_code quando il modello lo accetta; frasi → modello delle frasi.
/// </summary>
public sealed record ElevenLabsPlan(
    string VoiceId,
    string ModelId,
    string? LanguageCode,
    string OutputFormat,
    ElevenLabsVoiceSettings Settings)
{
    public const string DefaultSentenceModel = "eleven_multilingual_v2";
    public const string DefaultOutputFormat = "pcm_44100";
    /// <summary>Formato di ripiego quando il piano dell'account rifiuta il PCM a 44,1 kHz (riservato al piano Pro).</summary>
    public const string FallbackOutputFormat = "pcm_24000";

    public int SampleRate => SampleRateOf(OutputFormat);

    public static ElevenLabsPlan Create(SpeechSettings s, SpeechRequest request, string outputFormat)
    {
        string sentenceModel = string.IsNullOrWhiteSpace(s.ElevenLabsSentenceModel) ? DefaultSentenceModel : s.ElevenLabsSentenceModel.Trim();
        string labelModel = string.IsNullOrWhiteSpace(s.ElevenLabsLabelModel) ? sentenceModel : s.ElevenLabsLabelModel.Trim();
        string model = request.Kind == SpeechKind.Label ? labelModel : sentenceModel;

        string? language = request.LanguageHint;
        if (request.Kind == SpeechKind.Label)
        {
            language = s.LabelLanguage switch
            {
                LabelLanguageMode.Italian => "it",
                LabelLanguageMode.English => "en",
                _ => language
            };
        }
        string? languageCode = ModelSupportsLanguageCode(model) ? NormalizeLanguage(language) : null;

        return new ElevenLabsPlan(
            (s.ElevenLabsVoiceId ?? "").Trim(),
            model,
            languageCode,
            NormalizeOutputFormat(outputFormat),
            ElevenLabsVoiceSettings.FromSettings(s));
    }

    /// <summary>
    /// eleven_multilingual_v2 (e i modelli v1 e Flash/Turbo v2 solo inglese) non accettano language_code.
    /// Per i modelli sconosciuti si assume che lo accettino: la documentazione dice che un codice non supportato viene ignorato.
    /// </summary>
    public static bool ModelSupportsLanguageCode(string modelId)
    {
        string m = modelId.Trim().ToLowerInvariant();
        if (m.StartsWith("eleven_multilingual_v2", StringComparison.Ordinal)) return false;
        return m is not ("eleven_multilingual_v1" or "eleven_monolingual_v1" or "eleven_english_v1"
            or "eleven_flash_v2" or "eleven_turbo_v2" or "eleven_english_sts_v2" or "eleven_multilingual_sts_v2");
    }

    /// <summary>"it", "it-IT", "en_US" → "it", "en"; qualunque altra cosa → null (lingua lasciata al modello).</summary>
    public static string? NormalizeLanguage(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        string code = hint.Trim();
        int sep = code.IndexOfAny(['-', '_']);
        if (sep > 0) code = code[..sep];
        code = code.ToLowerInvariant();
        return code.Length is 2 or 3 && code.All(char.IsAsciiLetterLower) ? code : null;
    }

    /// <summary>Accetta solo i formati PCM grezzi conosciuti; qualunque altro valore torna a pcm_44100.</summary>
    public static string NormalizeOutputFormat(string? format)
    {
        string f = (format ?? "").Trim().ToLowerInvariant();
        return f is "pcm_8000" or "pcm_16000" or "pcm_22050" or "pcm_24000" or "pcm_32000" or "pcm_44100" or "pcm_48000"
            ? f
            : DefaultOutputFormat;
    }

    public static int SampleRateOf(string outputFormat)
    {
        string f = NormalizeOutputFormat(outputFormat);
        return int.Parse(f.AsSpan("pcm_".Length), NumberStyles.None, CultureInfo.InvariantCulture);
    }
}

/// <summary>Errore restituito dal server, già ridotto ai campi che servono (le forme del corpo variano: vedi ElevenLabsErrors).</summary>
public sealed record ElevenLabsError(int Status, string? Code, string? Message, string? RequestId);

// ---------------------------------------------------------------------------------------------------------------------
// DTO JSON interni (nomi snake_case come nella specifica OpenAPI)
// ---------------------------------------------------------------------------------------------------------------------

internal static class ElevenLabsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class TtsRequestBody
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = "";
    [JsonPropertyName("language_code")] public string? LanguageCode { get; set; }
    [JsonPropertyName("voice_settings")] public VoiceSettingsBody? VoiceSettings { get; set; }
    [JsonPropertyName("apply_text_normalization")] public string? ApplyTextNormalization { get; set; }
}

internal sealed class VoiceSettingsBody
{
    [JsonPropertyName("stability")] public double Stability { get; set; }
    [JsonPropertyName("similarity_boost")] public double SimilarityBoost { get; set; }
    [JsonPropertyName("style")] public double Style { get; set; }
    [JsonPropertyName("use_speaker_boost")] public bool UseSpeakerBoost { get; set; }
    [JsonPropertyName("speed")] public double Speed { get; set; }

    public static VoiceSettingsBody From(ElevenLabsVoiceSettings s) => new()
    {
        Stability = s.Stability,
        SimilarityBoost = s.SimilarityBoost,
        Style = s.Style,
        UseSpeakerBoost = s.UseSpeakerBoost,
        Speed = s.Speed
    };
}

internal sealed class VoicesPageDto
{
    [JsonPropertyName("voices")] public List<VoiceDto>? Voices { get; set; }
    [JsonPropertyName("has_more")] public bool? HasMore { get; set; }
    [JsonPropertyName("next_page_token")] public string? NextPageToken { get; set; }
}

internal sealed class VoiceDto
{
    [JsonPropertyName("voice_id")] public string? VoiceId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("labels")] public Dictionary<string, JsonElement>? Labels { get; set; }
    [JsonPropertyName("preview_url")] public string? PreviewUrl { get; set; }

    public ElevenLabsVoice? ToModel()
    {
        if (string.IsNullOrWhiteSpace(VoiceId)) return null;
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Labels is not null)
        {
            foreach (var (key, value) in Labels)
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) labels[key] = text;
            }
        }
        return new ElevenLabsVoice(VoiceId, string.IsNullOrWhiteSpace(Name) ? VoiceId : Name, Category, labels, PreviewUrl);
    }
}

internal sealed class SubscriptionDto
{
    [JsonPropertyName("tier")] public string? Tier { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("character_count")] public long? CharacterCount { get; set; }
    [JsonPropertyName("character_limit")] public long? CharacterLimit { get; set; }
    [JsonPropertyName("next_character_count_reset_unix")] public long? NextCharacterCountResetUnix { get; set; }

    public ElevenLabsSubscription ToModel() => new(
        Tier ?? "",
        Status ?? "",
        CharacterCount ?? 0,
        CharacterLimit ?? 0,
        NextCharacterCountResetUnix);
}

internal sealed class ModelDto
{
    [JsonPropertyName("model_id")] public string? ModelId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("can_do_text_to_speech")] public bool? CanDoTextToSpeech { get; set; }
    [JsonPropertyName("languages")] public List<ModelLanguageDto>? Languages { get; set; }
    [JsonPropertyName("maximum_text_length_per_request")] public int? MaximumTextLengthPerRequest { get; set; }
    [JsonPropertyName("max_characters_request_subscribed_user")] public int? MaxCharactersRequestSubscribedUser { get; set; }
    [JsonPropertyName("model_rates")] public ModelRatesDto? ModelRates { get; set; }

    public ElevenLabsModel? ToModel()
    {
        if (string.IsNullOrWhiteSpace(ModelId)) return null;
        var languages = (Languages ?? [])
            .Select(l => l.LanguageId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
        return new ElevenLabsModel(
            ModelId,
            string.IsNullOrWhiteSpace(Name) ? ModelId : Name,
            CanDoTextToSpeech ?? false,
            languages,
            MaxCharactersRequestSubscribedUser ?? MaximumTextLengthPerRequest,
            ModelRates?.CharacterCostMultiplier);
    }
}

internal sealed class ModelLanguageDto
{
    [JsonPropertyName("language_id")] public string? LanguageId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class ModelRatesDto
{
    [JsonPropertyName("character_cost_multiplier")] public double? CharacterCostMultiplier { get; set; }
}
