using System.Net;
using System.Text.Json;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Speech.ElevenLabs;
using PuntaEAscolta.Speech.Tests.Fakes;
using Xunit;

namespace PuntaEAscolta.Speech.Tests;

public sealed class ElevenLabsSynthesizerTests
{
    private static (ElevenLabsSynthesizer Synth, FakeHttpHandler Http, FakeSettingsStore Settings, CollectingLog Log) Create(
        Func<CapturedRequest, int, HttpResponseMessage> responder, Action<AppSettings>? tweak = null)
    {
        var http = new FakeHttpHandler(responder);
        var settings = FakeSettingsStore.WithElevenLabs(tweak);
        var log = new CollectingLog();
        var synth = new ElevenLabsSynthesizer(new HttpClient(http), settings, new FakeProtector(), log);
        return (synth, http, settings, log);
    }

    private static async Task<byte[]> ReadAllAsync(SpeechAudio audio)
    {
        await using (audio)
        {
            using var buffer = new MemoryStream();
            await audio.Pcm.CopyToAsync(buffer);
            return buffer.ToArray();
        }
    }

    [Fact]
    public async Task Label_request_has_stream_url_key_header_and_flash_body_with_language_code()
    {
        var (synth, http, _, _) = Create((_, _) => FakeHttpHandler.Audio(TestUtil.Pcm("audio")));

        var audio = await synth.SynthesizeAsync(new SpeechRequest("Salva", SpeechKind.Label, "it"), CancellationToken.None);
        byte[] pcm = await ReadAllAsync(audio);

        Assert.Equal("audio", TestUtil.Text(pcm));
        Assert.Equal(44100, audio.Format.SampleRate);
        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://api.elevenlabs.io/v1/text-to-speech/{TestKeys.VoiceId}/stream?output_format=pcm_44100", request.Uri.ToString());
        Assert.Equal(TestKeys.ApiKey, request.Header("xi-api-key"));

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("Salva", root.GetProperty("text").GetString());
        Assert.Equal("eleven_flash_v2_5", root.GetProperty("model_id").GetString());
        Assert.Equal("it", root.GetProperty("language_code").GetString());
        var voice = root.GetProperty("voice_settings");
        Assert.Equal(0.5, voice.GetProperty("stability").GetDouble());
        Assert.Equal(0.75, voice.GetProperty("similarity_boost").GetDouble());
        Assert.Equal(0.0, voice.GetProperty("style").GetDouble());
        Assert.True(voice.GetProperty("use_speaker_boost").GetBoolean());
        Assert.Equal(1.0, voice.GetProperty("speed").GetDouble());
        Assert.Equal(5, synth.LastCharacterCost);
    }

    [Fact]
    public async Task Sentence_uses_multilingual_v2_without_language_code()
    {
        var (synth, http, _, _) = Create((_, _) => FakeHttpHandler.Audio(TestUtil.Pcm("x")));

        await ReadAllAsync(await synth.SynthesizeAsync(new SpeechRequest("Una frase.", SpeechKind.Sentence, "it"), CancellationToken.None));

        using var body = JsonDocument.Parse(Assert.Single(http.Requests).Body!);
        Assert.Equal("eleven_multilingual_v2", body.RootElement.GetProperty("model_id").GetString());
        Assert.False(body.RootElement.TryGetProperty("language_code", out _));
    }

    [Fact]
    public async Task Label_language_mode_forces_english_code()
    {
        var (synth, http, _, _) = Create((_, _) => FakeHttpHandler.Audio(TestUtil.Pcm("x")),
            s => s.Speech.LabelLanguage = LabelLanguageMode.English);

        await ReadAllAsync(await synth.SynthesizeAsync(new SpeechRequest("Salva", SpeechKind.Label, "it"), CancellationToken.None));

        using var body = JsonDocument.Parse(Assert.Single(http.Requests).Body!);
        Assert.Equal("en", body.RootElement.GetProperty("language_code").GetString());
    }

    [Theory]
    // Famiglia di nomi attuale
    [InlineData(401, """{"detail":{"type":"authentication_error","code":"invalid_api_key","message":"Invalid API key"}}""", SpeechProviderReason.InvalidKey)]
    [InlineData(401, """{"detail":{"code":"missing_api_key","message":"x"}}""", SpeechProviderReason.InvalidKey)]
    [InlineData(402, """{"detail":{"code":"insufficient_credits","message":"x"}}""", SpeechProviderReason.QuotaExceeded)]
    [InlineData(429, """{"detail":{"code":"rate_limit_exceeded","message":"x"}}""", SpeechProviderReason.RateLimited)]
    [InlineData(429, """{"detail":{"code":"concurrent_limit_exceeded","message":"x"}}""", SpeechProviderReason.RateLimited)]
    [InlineData(429, """{"detail":{"code":"system_busy","message":"x"}}""", SpeechProviderReason.RateLimited)]
    // Famiglia di nomi storica: l'identificatore vince sul codice HTTP
    [InlineData(401, """{"detail":{"status":"quota_exceeded","message":"This request exceeds your quota"}}""", SpeechProviderReason.QuotaExceeded)]
    [InlineData(400, """{"detail":{"status":"quota_exceeded","message":"x"}}""", SpeechProviderReason.QuotaExceeded)]
    [InlineData(429, """{"detail":{"status":"too_many_concurrent_requests","message":"x"}}""", SpeechProviderReason.RateLimited)]
    [InlineData(401, """{"detail":{"status":"invalid_api_key","message":"x"}}""", SpeechProviderReason.InvalidKey)]
    // Solo codice HTTP o corpi diversi
    [InlineData(401, "", SpeechProviderReason.InvalidKey)]
    [InlineData(402, "not json", SpeechProviderReason.QuotaExceeded)]
    [InlineData(500, """{"detail":"Internal error"}""", SpeechProviderReason.Server)]
    [InlineData(503, "", SpeechProviderReason.Server)]
    [InlineData(404, """{"detail":{"code":"voice_not_found","message":"Voice not found"}}""", SpeechProviderReason.Configuration)]
    [InlineData(422, """{"detail":[{"loc":["body","text"],"msg":"field required","type":"missing"}]}""", SpeechProviderReason.Configuration)]
    public async Task Http_errors_map_to_typed_reasons(int status, string body, SpeechProviderReason expected)
    {
        var (synth, http, _, log) = Create((_, _) => FakeHttpHandler.Json((HttpStatusCode)status, body));

        var ex = await Assert.ThrowsAsync<SpeechProviderException>(() =>
            synth.SynthesizeAsync(new SpeechRequest("Ciao", SpeechKind.Label, "it"), CancellationToken.None));

        Assert.Equal(expected, ex.Reason);
        Assert.Equal(status, ex.HttpStatus);
        Assert.Single(http.Requests); // nessun tentativo ripetuto (non è un rifiuto del formato)
        Assert.DoesNotContain(TestKeys.ApiKey, ex.Message);
        Assert.False(log.Contains(TestKeys.ApiKey));
    }

    [Fact]
    public async Task Rejected_pcm_44100_retries_once_with_pcm_24000_and_remembers_until_settings_change()
    {
        var (synth, http, settings, log) = Create((request, _) =>
            request.Uri.Query.Contains("pcm_44100")
                ? FakeHttpHandler.Json(HttpStatusCode.Forbidden, """{"detail":{"code":"invalid_output_format","message":"PCM with 44.1kHz sample rate requires Pro tier"}}""")
                : FakeHttpHandler.Audio(TestUtil.Pcm("24k")));

        var first = await synth.SynthesizeAsync(new SpeechRequest("Uno", SpeechKind.Label), CancellationToken.None);
        Assert.Equal(24000, first.Format.SampleRate);
        Assert.Equal("24k", TestUtil.Text(await ReadAllAsync(first)));
        Assert.Equal(2, http.Count);
        Assert.Contains("output_format=pcm_24000", http.Requests[1].Uri.Query);
        Assert.Equal("pcm_24000", synth.EffectiveOutputFormat);

        // Ricordato: la richiesta successiva va diretta a pcm_24000.
        await ReadAllAsync(await synth.SynthesizeAsync(new SpeechRequest("Due", SpeechKind.Label), CancellationToken.None));
        Assert.Equal(3, http.Count);
        Assert.Contains("output_format=pcm_24000", http.Requests[2].Uri.Query);

        // La chiave di cache usa il formato configurato: la stessa etichetta non si scarica due volte.
        Assert.Equal("pcm_44100", synth.DescribeForCache(new SpeechRequest("Due", SpeechKind.Label)).OutputFormat);

        settings.Save(settings.Current);
        Assert.Equal("pcm_44100", synth.EffectiveOutputFormat);
        Assert.False(log.Contains(TestKeys.ApiKey));
    }

    [Fact]
    public async Task Format_rejection_with_fallback_also_rejected_is_a_configuration_error()
    {
        var (synth, http, _, _) = Create((_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"detail":{"code":"invalid_output_format","message":"bad output_format"}}"""));

        var ex = await Assert.ThrowsAsync<SpeechProviderException>(() =>
            synth.SynthesizeAsync(new SpeechRequest("Uno", SpeechKind.Label), CancellationToken.None));

        Assert.Equal(SpeechProviderReason.Configuration, ex.Reason);
        Assert.Equal(2, http.Count);
    }

    [Fact]
    public async Task Transport_failures_map_to_network_and_timeout()
    {
        var network = new ElevenLabsSynthesizer(
            new HttpClient(new FakeHttpHandler((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("Host sconosciuto")))),
            FakeSettingsStore.WithElevenLabs(), new FakeProtector(), new CollectingLog());
        var ex1 = await Assert.ThrowsAsync<SpeechProviderException>(() => network.SynthesizeAsync(new SpeechRequest("a", SpeechKind.Label), CancellationToken.None));
        Assert.Equal(SpeechProviderReason.Network, ex1.Reason);

        var timeout = new ElevenLabsSynthesizer(
            new HttpClient(new FakeHttpHandler(async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return FakeHttpHandler.Audio([]);
            }))
            { Timeout = TimeSpan.FromMilliseconds(100) },
            FakeSettingsStore.WithElevenLabs(), new FakeProtector(), new CollectingLog());
        var ex2 = await Assert.ThrowsAsync<SpeechProviderException>(() => timeout.SynthesizeAsync(new SpeechRequest("a", SpeechKind.Label), CancellationToken.None));
        Assert.Equal(SpeechProviderReason.Timeout, ex2.Reason);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_mapped()
    {
        var synth = new ElevenLabsSynthesizer(
            new HttpClient(new FakeHttpHandler(async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return FakeHttpHandler.Audio([]);
            })),
            FakeSettingsStore.WithElevenLabs(), new FakeProtector(), new CollectingLog());
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synth.SynthesizeAsync(new SpeechRequest("a", SpeechKind.Label), cts.Token));
    }

    [Fact]
    public async Task Missing_key_or_voice_is_not_configured_without_http()
    {
        var http = new FakeHttpHandler((_, _) => FakeHttpHandler.Audio([]));
        var noKey = new ElevenLabsSynthesizer(new HttpClient(http), new FakeSettingsStore(), new FakeProtector(), new CollectingLog());
        Assert.False(noKey.IsConfigured);
        var ex = await Assert.ThrowsAsync<SpeechProviderException>(() => noKey.SynthesizeAsync(new SpeechRequest("a", SpeechKind.Label), CancellationToken.None));
        Assert.Equal(SpeechProviderReason.NotConfigured, ex.Reason);

        var noVoice = new ElevenLabsSynthesizer(new HttpClient(http), FakeSettingsStore.WithElevenLabs(s => s.Speech.ElevenLabsVoiceId = ""),
            new FakeProtector(), new CollectingLog());
        Assert.False(noVoice.IsConfigured);

        var undecryptable = new ElevenLabsSynthesizer(new HttpClient(http),
            FakeSettingsStore.WithElevenLabs(s => s.Speech.ElevenLabsProtectedApiKey = "copiata-da-un-altro-pc"), new FakeProtector(), new CollectingLog());
        Assert.False(undecryptable.IsConfigured);
        Assert.Equal(0, http.Count);
    }

    [Fact]
    public void Plan_rules_for_language_code_models_and_formats()
    {
        Assert.False(ElevenLabsPlan.ModelSupportsLanguageCode("eleven_multilingual_v2"));
        Assert.True(ElevenLabsPlan.ModelSupportsLanguageCode("eleven_flash_v2_5"));
        Assert.True(ElevenLabsPlan.ModelSupportsLanguageCode("eleven_v3"));
        Assert.False(ElevenLabsPlan.ModelSupportsLanguageCode("eleven_flash_v2"));
        Assert.Equal("it", ElevenLabsPlan.NormalizeLanguage("it-IT"));
        Assert.Equal("en", ElevenLabsPlan.NormalizeLanguage("en_US"));
        Assert.Null(ElevenLabsPlan.NormalizeLanguage("italiano"));
        Assert.Equal("pcm_44100", ElevenLabsPlan.NormalizeOutputFormat("mp3_44100_128"));
        Assert.Equal(24000, ElevenLabsPlan.SampleRateOf("pcm_24000"));

        var speech = new SpeechSettings { ElevenLabsLabelModel = "", ElevenLabsSpeed = 5, ElevenLabsStability = double.NaN };
        var plan = ElevenLabsPlan.Create(speech, new SpeechRequest("x", SpeechKind.Label, "it"), "pcm_44100");
        Assert.Equal("eleven_multilingual_v2", plan.ModelId); // modello etichette vuoto → modello frasi
        Assert.Null(plan.LanguageCode);
        Assert.Equal(1.2, plan.Settings.Speed);
        Assert.Equal(0.5, plan.Settings.Stability);
    }

    [Fact]
    public void Error_parser_accepts_all_known_body_shapes()
    {
        var modern = ElevenLabsErrors.Parse(401, """{"detail":{"type":"authentication_error","code":"invalid_api_key","message":"m","request_id":"r1"}}""");
        Assert.Equal(("invalid_api_key", "m", "r1"), (modern.Code, modern.Message, modern.RequestId));

        var legacy = ElevenLabsErrors.Parse(401, """{"detail":{"status":"Quota_Exceeded","message":"m"}}""");
        Assert.Equal("quota_exceeded", legacy.Code);

        var validation = ElevenLabsErrors.Parse(422, """{"detail":[{"msg":"a"},{"msg":"b"}]}""");
        Assert.Equal(("validation_error", "a; b"), (validation.Code, validation.Message));

        var text = ElevenLabsErrors.Parse(400, """{"detail":"solo testo"}""");
        Assert.Equal((null, "solo testo"), (text.Code, text.Message));

        var garbage = ElevenLabsErrors.Parse(500, "<html>errore</html>");
        Assert.Equal("<html>errore</html>", garbage.Message);

        Assert.True(ElevenLabsErrors.IsOutputFormatRejection(new ElevenLabsError(403, null, "PCM with 44.1kHz requires Pro", null)));
        Assert.False(ElevenLabsErrors.IsOutputFormatRejection(new ElevenLabsError(401, "invalid_api_key", "pcm", null)));
        Assert.False(ElevenLabsErrors.IsOutputFormatRejection(new ElevenLabsError(500, null, "pcm", null)));
    }
}
