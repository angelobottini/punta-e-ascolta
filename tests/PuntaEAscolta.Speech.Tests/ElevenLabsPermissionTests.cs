using System.Net;
using System.Text.Json;
using PuntaEAscolta.Speech.ElevenLabs;
using PuntaEAscolta.Speech.Playback;
using PuntaEAscolta.Speech.Tests.Fakes;
using Xunit;

namespace PuntaEAscolta.Speech.Tests;

/// <summary>
/// Prove dal vivo del 23/09/2026: una chiave ElevenLabs limitata al solo Text to Speech riceve 401 missing_permissions su
/// /v1/user/subscription e la finestra diceva "ElevenLabs ha rifiutato la chiave". Chiavi con permessi limitati e pulizia
/// della chiave incollata.
/// </summary>
public sealed class ElevenLabsPermissionTests
{
    private const string SubscriptionJson =
        """{"tier":"pro","status":"active","character_count":1000,"character_limit":600000,"next_character_count_reset_unix":1790000000}""";

    private const string VoicesJson = """{"voices":[{"voice_id":"voce_it_123","name":"Bianca"}],"has_more":false}""";

    private static HttpResponseMessage MissingPermission(string permission) => FakeHttpHandler.Json(HttpStatusCode.Unauthorized,
        "{\"detail\":{\"status\":\"missing_permissions\",\"message\":\"The API key you used is missing the permission " + permission +
        " to execute this operation.\"}}");

    private static HttpResponseMessage InvalidKey() =>
        FakeHttpHandler.Json(HttpStatusCode.Unauthorized, """{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}""");

    /// <summary>Server finto a tre vie: abbonamento, voci (v2 e v1), sintesi.</summary>
    private static FakeHttpHandler Server(
        Func<HttpResponseMessage> subscription,
        Func<HttpResponseMessage> voices,
        Func<HttpResponseMessage>? speech = null) =>
        new((request, _) => request.Uri.AbsolutePath switch
        {
            "/v1/user/subscription" => subscription(),
            "/v2/voices" or "/v1/voices" => voices(),
            var path when path.StartsWith("/v1/text-to-speech/", StringComparison.Ordinal) =>
                (speech ?? (() => FakeHttpHandler.Audio(TestUtil.Pcm("pcm"))))(),
            _ => FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"detail":"Not found"}"""),
        });

    private static ElevenLabsAccountClient Client(FakeHttpHandler http) => new(new HttpClient(http));

    // -----------------------------------------------------------------------------------------------------------------
    // Mappatura degli errori
    // -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(401, """{"detail":{"status":"missing_permissions","message":"The API key you used is missing the permission user_read to execute this operation."}}""", SpeechProviderReason.MissingPermission, "user_read")]
    [InlineData(401, """{"detail":{"code":"missing_permissions","message":"x"}}""", SpeechProviderReason.MissingPermission, null)]
    [InlineData(403, """{"detail":{"code":"forbidden","message":"The API key is missing the permission voices_read."}}""", SpeechProviderReason.MissingPermission, "voices_read")]
    [InlineData(401, """{"detail":{"message":"Error: missing_permissions: text_to_speech"}}""", SpeechProviderReason.MissingPermission, "text_to_speech")]
    [InlineData(401, """{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}""", SpeechProviderReason.InvalidKey, null)]
    [InlineData(401, "", SpeechProviderReason.InvalidKey, null)]
    [InlineData(400, """{"detail":{"message":"missing the permission user_read"}}""", SpeechProviderReason.Configuration, null)]
    public void Errors_with_missing_permissions_are_not_an_invalid_key(int status, string body, SpeechProviderReason expected, string? permission)
    {
        var error = ElevenLabsErrors.Parse(status, body);

        Assert.Equal(expected, ElevenLabsErrors.Map(error));
        var ex = ElevenLabsErrors.ToException(error);
        Assert.Equal(expected, ex.Reason);
        Assert.Equal(permission, ex.MissingPermission);
    }

    [Theory]
    [InlineData("The API key you used is missing the permission user_read to execute this operation.", "user_read")]
    [InlineData("missing the permission 'voices_read'", "voices_read")]
    [InlineData("Missing permissions: TEXT_TO_SPEECH", "text_to_speech")]
    [InlineData("missing_permissions to execute this operation", null)]
    [InlineData("missing the permission for this", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Permission_name_is_parsed_only_when_named(string? message, string? expected)
    {
        Assert.Equal(expected, ElevenLabsErrors.ParseMissingPermission(message));
    }

    [Fact]
    public void Missing_permission_is_never_taken_for_an_output_format_rejection()
    {
        var error = ElevenLabsErrors.Parse(403,
            """{"detail":{"status":"missing_permissions","message":"The API key is missing the permission text_to_speech (pcm output_format)."}}""");

        Assert.False(ElevenLabsErrors.IsOutputFormatRejection(error));
    }

    [Fact]
    public void Exception_keeps_the_redacted_server_message()
    {
        var error = ElevenLabsErrors.Redact(ElevenLabsErrors.Parse(401,
            "{\"detail\":{\"status\":\"missing_permissions\",\"message\":\"Key " + TestKeys.ApiKey + " is missing the permission user_read.\"}}"),
            TestKeys.ApiKey);

        var ex = ElevenLabsErrors.ToException(error);

        Assert.Equal("user_read", ex.MissingPermission);
        Assert.NotNull(ex.ServerMessage);
        Assert.DoesNotContain(TestKeys.ApiKey, ex.ServerMessage);
        Assert.DoesNotContain(TestKeys.ApiKey, ex.Message);
        Assert.Contains("user_read", ElevenLabsMessages.Explain(ex));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Verifica della chiave
    // -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Key_without_user_read_is_valid_and_can_speak()
    {
        var http = Server(() => MissingPermission("user_read"), () => FakeHttpHandler.Json(HttpStatusCode.OK, VoicesJson));

        var check = await Client(http).CheckKeyAsync(TestKeys.ApiKey, TestKeys.VoiceId, CancellationToken.None);

        Assert.True(check.Valid);
        Assert.Null(check.Subscription);
        Assert.Equal("Bianca", Assert.Single(check.Voices!).Name);
        Assert.Equal(["user_read"], check.MissingPermissions);
        Assert.True(check.TtsTested);
        Assert.False(check.CannotSpeak);
        Assert.StartsWith("Chiave valida.", check.Message);
        Assert.Contains("(user_read): i crediti non si possono mostrare", check.Message);
        Assert.Contains("Prova di lettura riuscita.", check.Message);

        // La prova di lettura: pochi caratteri, modello Flash, PCM a 16 kHz, con la chiave nell'intestazione.
        var speech = Assert.Single(http.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal($"https://api.elevenlabs.io/v1/text-to-speech/{TestKeys.VoiceId}/stream?output_format=pcm_16000", speech.Uri.ToString());
        Assert.Equal(TestKeys.ApiKey, speech.Header("xi-api-key"));
        using var body = JsonDocument.Parse(speech.Body!);
        Assert.Equal("Prova", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("eleven_flash_v2_5", body.RootElement.GetProperty("model_id").GetString());
    }

    [Fact]
    public async Task Rejected_key_is_not_valid_and_nothing_else_is_asked()
    {
        var http = Server(InvalidKey, () => FakeHttpHandler.Json(HttpStatusCode.OK, VoicesJson));

        var check = await Client(http).CheckKeyAsync(TestKeys.ApiKey, TestKeys.VoiceId, CancellationToken.None);

        Assert.False(check.Valid);
        Assert.Empty(check.MissingPermissions);
        Assert.Null(check.TtsTested);
        Assert.Contains("ha rifiutato la chiave", check.Message);
        Assert.Single(http.Requests);
        Assert.False(await Client(http).ValidateKeyAsync(TestKeys.ApiKey, CancellationToken.None));
    }

    [Fact]
    public async Task Key_without_voices_read_is_valid_without_voice_list_and_v1_is_not_tried()
    {
        var http = Server(() => FakeHttpHandler.Json(HttpStatusCode.OK, SubscriptionJson), () => MissingPermission("voices_read"));

        var check = await Client(http).CheckKeyAsync(TestKeys.ApiKey, voiceId: null, CancellationToken.None);

        Assert.True(check.Valid);
        Assert.Equal("pro", check.Subscription!.Tier);
        Assert.Null(check.Voices);
        Assert.Equal(["voices_read"], check.MissingPermissions);
        Assert.Null(check.TtsTested);
        Assert.Contains("(voices_read)", check.Message);
        Assert.Contains("manca l'ID della voce", check.Message);
        Assert.DoesNotContain(http.Requests, r => r.Uri.AbsolutePath == "/v1/voices");
        Assert.DoesNotContain(http.Requests, r => r.Method == HttpMethod.Post); // senza voce nessuna prova di lettura
        Assert.True(await Client(http).ValidateKeyAsync(TestKeys.ApiKey, CancellationToken.None));
    }

    [Fact]
    public async Task Key_without_text_to_speech_is_reported_as_unable_to_speak()
    {
        var http = Server(() => MissingPermission("user_read"), () => MissingPermission("voices_read"), () => MissingPermission("text_to_speech"));

        var check = await Client(http).CheckKeyAsync(TestKeys.ApiKey, TestKeys.VoiceId, CancellationToken.None);

        Assert.True(check.Valid); // il server ha riconosciuto la chiave: le mancano solo dei permessi
        Assert.Equal(["user_read", "voices_read", "text_to_speech"], check.MissingPermissions);
        Assert.False(check.TtsTested);
        Assert.Equal(SpeechProviderReason.MissingPermission, check.TtsFailure);
        Assert.True(check.CannotSpeak);
        Assert.Contains("(text_to_speech): con questa chiave ElevenLabs non può leggere", check.Message);
    }

    [Fact]
    public async Task Voice_not_found_and_quota_are_explained_in_italian()
    {
        var notFound = Server(() => FakeHttpHandler.Json(HttpStatusCode.OK, SubscriptionJson), () => FakeHttpHandler.Json(HttpStatusCode.OK, VoicesJson),
            () => FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"detail":{"code":"voice_not_found","message":"A voice with the voice_id xyz was not found."}}"""));
        var check = await Client(notFound).CheckKeyAsync(TestKeys.ApiKey, "xyz", CancellationToken.None);
        Assert.True(check.Valid);
        Assert.False(check.TtsTested);
        Assert.Equal(SpeechProviderReason.Configuration, check.TtsFailure);
        Assert.Contains("la voce con questo ID non esiste", check.Message);
        Assert.Contains("was not found", check.Message); // messaggio del server fra parentesi

        var quota = Server(() => FakeHttpHandler.Json(HttpStatusCode.OK, SubscriptionJson), () => FakeHttpHandler.Json(HttpStatusCode.OK, VoicesJson),
            () => FakeHttpHandler.Json(HttpStatusCode.PaymentRequired, """{"detail":{"code":"insufficient_credits","message":"no credits"}}"""));
        check = await Client(quota).CheckKeyAsync(TestKeys.ApiKey, TestKeys.VoiceId, CancellationToken.None);
        Assert.False(check.TtsTested);
        Assert.Contains("crediti ElevenLabs esauriti", check.Message);
    }

    [Fact]
    public async Task Network_problem_on_first_call_is_raised_not_reported_as_rejected_key()
    {
        var client = new ElevenLabsAccountClient(new HttpClient(new FakeHttpHandler((_, _, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("rete assente")))));

        var ex = await Assert.ThrowsAsync<SpeechProviderException>(() => client.CheckKeyAsync(TestKeys.ApiKey, TestKeys.VoiceId, CancellationToken.None));
        Assert.Equal(SpeechProviderReason.Network, ex.Reason);
        Assert.Contains("rete", ElevenLabsMessages.Explain(ex));
    }

    [Fact]
    public async Task Key_never_appears_in_the_check_message_even_if_the_server_repeats_it()
    {
        string echo = "{\"detail\":{\"status\":\"missing_permissions\",\"message\":\"Key " + TestKeys.ApiKey + " is missing the permission user_read.\"}}";
        string rejected = "{\"detail\":{\"status\":\"invalid_api_key\",\"message\":\"Key " + TestKeys.ApiKey + " is not valid.\"}}";
        var limited = Server(() => FakeHttpHandler.Json(HttpStatusCode.Unauthorized, echo), () => FakeHttpHandler.Json(HttpStatusCode.OK, VoicesJson),
            () => FakeHttpHandler.Json(HttpStatusCode.NotFound, "{\"detail\":{\"code\":\"voice_not_found\",\"message\":\"" + TestKeys.ApiKey + "\"}}"));
        var invalid = Server(() => FakeHttpHandler.Json(HttpStatusCode.Unauthorized, rejected), () => FakeHttpHandler.Json(HttpStatusCode.OK, VoicesJson));

        var first = await Client(limited).CheckKeyAsync(TestKeys.ApiKey, TestKeys.VoiceId, CancellationToken.None);
        var second = await Client(invalid).CheckKeyAsync(TestKeys.ApiKey, TestKeys.VoiceId, CancellationToken.None);

        Assert.DoesNotContain(TestKeys.ApiKey, first.Message);
        Assert.All(first.Notes, n => Assert.DoesNotContain(TestKeys.ApiKey, n));
        Assert.DoesNotContain(TestKeys.ApiKey, second.Message);
    }

    [Fact]
    public async Task Blank_key_is_not_valid_and_makes_no_request()
    {
        var http = Server(() => FakeHttpHandler.Json(HttpStatusCode.OK, SubscriptionJson), () => FakeHttpHandler.Json(HttpStatusCode.OK, VoicesJson));

        var check = await Client(http).CheckKeyAsync("  ", TestKeys.VoiceId, CancellationToken.None);

        Assert.False(check.Valid);
        Assert.Empty(http.Requests);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Interruttore automatico
    // -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Breaker_treats_missing_permission_like_a_rejected_key_and_logs_it_once()
    {
        var time = new ManualTimeProvider();
        var log = new CollectingLog();
        var breaker = new CloudCircuitBreaker(time, log);

        breaker.RecordFailure(SpeechProviderReason.MissingPermission, "text_to_speech");
        breaker.RecordFailure(SpeechProviderReason.MissingPermission, "text_to_speech");

        Assert.False(breaker.IsAvailable);
        Assert.Equal(DateTimeOffset.MaxValue, breaker.SuspendedUntil);
        time.Advance(TimeSpan.FromDays(1));
        Assert.False(breaker.IsAvailable);
        Assert.Single(log.Entries, e => e.StartsWith("WARN ", StringComparison.Ordinal) && e.Contains("text_to_speech", StringComparison.Ordinal));

        breaker.Reset();
        Assert.True(breaker.IsAvailable);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Pulizia della chiave incollata
    // -----------------------------------------------------------------------------------------------------------------

    private const string Key = "sk_0123456789abcdef0123456789abcdef";

    public static TheoryData<string> DirtyKeys()
    {
        string zwsp = ((char)0x200B).ToString();
        string bom = ((char)0xFEFF).ToString();
        string nbsp = ((char)0x00A0).ToString();
        string openQuote = ((char)0x201C).ToString();
        string closeQuote = ((char)0x201D).ToString();
        return new TheoryData<string>
        {
            Key,
            "  " + Key + "  ",
            Key + "\r\n",
            "\t" + Key,
            bom + Key,
            Key + zwsp,
            "sk_0123456789" + zwsp + "abcdef0123456789abcdef",
            nbsp + Key + nbsp,
            "\"" + Key + "\"",
            "'" + Key + "'",
            "`" + Key + "`",
            openQuote + Key + closeQuote,
            "xi-api-key: " + Key,
            "XI-API-KEY=" + Key,
            "xi-api-key: \"" + Key + "\"",
            "\"xi-api-key\": \"" + Key + "\"",
            bom + "  \"xi-api-key:" + Key + "\"  " + zwsp,
        };
    }

    [Theory]
    [MemberData(nameof(DirtyKeys))]
    public void Pasted_key_is_cleaned(string pasted)
    {
        Assert.True(ElevenLabsKey.TrySanitize(pasted, out string key, out string? error), error);
        Assert.Equal(Key, key);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")] // chiave vecchia, senza sk_
    [InlineData("sk-ABC_def-123")]
    public void Keys_with_letters_digits_underscore_and_dash_are_accepted(string pasted)
    {
        Assert.True(ElevenLabsKey.TrySanitize(pasted, out string key, out _));
        Assert.Equal(pasted, key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("xi-api-key:")]
    [InlineData("sk_0123 4567")]
    [InlineData("sk_0123456789abcdef!")]
    [InlineData("sk_01234567è89")]
    [InlineData("Bearer sk_0123456789")]
    public void Unusable_keys_are_refused_with_an_italian_message_without_the_key(string? pasted)
    {
        Assert.False(ElevenLabsKey.TrySanitize(pasted, out string key, out string? error));
        Assert.Equal("", key);
        Assert.False(string.IsNullOrWhiteSpace(error));
        if (!string.IsNullOrWhiteSpace(pasted)) Assert.DoesNotContain(pasted.Trim(), error);
    }

    [Fact]
    public void Too_long_key_is_refused()
    {
        Assert.False(ElevenLabsKey.TrySanitize("sk_" + new string('a', ElevenLabsKey.MaxLength), out _, out string? error));
        Assert.Contains("troppo lunga", error);
    }

    [Fact]
    public void Spaces_inside_the_key_are_named_in_the_message()
    {
        Assert.False(ElevenLabsKey.TrySanitize("sk_0123 4567", out _, out string? error));
        Assert.Contains("spazi", error);
    }
}
