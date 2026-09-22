using System.Net;
using System.Text;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Speech.Dictation;
using PuntaEAscolta.Speech.Tests.Fakes;
using Xunit;

namespace PuntaEAscolta.Speech.Tests;

public sealed class ElevenLabsSpeechToTextTests
{
    private static readonly byte[] OneSecond = TestUtil.Repeat(5, 32000);

    private static (ElevenLabsSpeechToText Stt, FakeHttpHandler Http, CollectingLog Log) Create(
        Func<CapturedRequest, int, HttpResponseMessage> responder, Action<AppSettings>? tweak = null)
    {
        var http = new FakeHttpHandler(responder);
        var log = new CollectingLog();
        var stt = new ElevenLabsSpeechToText(new HttpClient(http), FakeSettingsStore.WithElevenLabs(tweak), new FakeProtector(), log);
        return (stt, http, log);
    }

    private static HttpResponseMessage Transcript(string text) =>
        FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"language_code":"ita","language_probability":0.98,"text":"{{text}}","words":[]}""");

    [Fact]
    public async Task Request_is_multipart_with_raw_pcm_and_documented_fields()
    {
        var (stt, http, log) = Create((_, _) => Transcript("Ciao mondo"));

        string text = await stt.TranscribeAsync(OneSecond, "it", CancellationToken.None);

        Assert.Equal("Ciao mondo", text);
        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text", request.Uri.ToString());
        Assert.Equal(TestKeys.ApiKey, request.Header("xi-api-key"));
        Assert.StartsWith("multipart/form-data", request.Header("Content-Type"));
        Assert.Equal("scribe_v2", request.Part("model_id")!.Text);
        Assert.Equal("it", request.Part("language_code")!.Text);
        Assert.Equal("false", request.Part("tag_audio_events")!.Text);
        Assert.Equal("false", request.Part("diarize")!.Text);
        Assert.Equal("pcm_s16le_16", request.Part("file_format")!.Text);
        var file = request.Part("file")!;
        Assert.Equal("audio.pcm", file.FileName);
        Assert.Equal(OneSecond, file.Data);
        Assert.False(log.Contains(TestKeys.ApiKey));
    }

    [Fact]
    public async Task Unknown_scribe_v2_falls_back_once_to_scribe_v1_and_remembers()
    {
        var (stt, http, _) = Create((request, _) => request.Part("model_id")!.Text == "scribe_v2"
            ? FakeHttpHandler.Json(HttpStatusCode.UnprocessableEntity, """{"detail":[{"loc":["body","model_id"],"msg":"Invalid model_id scribe_v2","type":"value_error"}]}""")
            : Transcript("ok"));

        Assert.Equal("ok", await stt.TranscribeAsync(OneSecond, "it", CancellationToken.None));
        Assert.Equal(["scribe_v2", "scribe_v1"], http.Requests.Select(r => r.Part("model_id")!.Text));

        Assert.Equal("ok", await stt.TranscribeAsync(OneSecond, "it", CancellationToken.None));
        Assert.Equal("scribe_v1", http.Requests[2].Part("model_id")!.Text);
        Assert.Equal(3, http.Count);
    }

    [Fact]
    public async Task Rejected_raw_pcm_is_resent_once_as_wav()
    {
        var (stt, http, _) = Create((request, _) => request.Part("file_format") is not null
            ? FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"detail":{"code":"invalid_file_format","message":"Unsupported file_format"}}""")
            : Transcript("dal wav"));

        Assert.Equal("dal wav", await stt.TranscribeAsync(OneSecond, "it", CancellationToken.None));

        var wav = http.Requests[1].Part("file")!;
        Assert.Equal("audio.wav", wav.FileName);
        Assert.Equal("audio/wav", wav.ContentType);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav.Data, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav.Data, 8, 4));
        Assert.Equal(44 + OneSecond.Length, wav.Data.Length);
        Assert.Equal(16000, BitConverter.ToInt32(wav.Data, 24));
    }

    [Theory]
    [InlineData(401, """{"detail":{"code":"invalid_api_key","message":"x"}}""", SpeechToTextFailure.InvalidKey)]
    [InlineData(402, """{"detail":{"code":"insufficient_credits","message":"x"}}""", SpeechToTextFailure.QuotaExceeded)]
    [InlineData(401, """{"detail":{"status":"quota_exceeded","message":"x"}}""", SpeechToTextFailure.QuotaExceeded)]
    [InlineData(400, """{"detail":{"status":"quota_exceeded","message":"x"}}""", SpeechToTextFailure.QuotaExceeded)]
    [InlineData(429, """{"detail":{"status":"too_many_concurrent_requests","message":"x"}}""", SpeechToTextFailure.RateLimited)]
    [InlineData(429, """{"detail":{"code":"rate_limit_exceeded","message":"x"}}""", SpeechToTextFailure.RateLimited)]
    [InlineData(403, """{"detail":{"code":"missing_permissions","message":"x"}}""", SpeechToTextFailure.Forbidden)]
    [InlineData(500, "", SpeechToTextFailure.Server)]
    [InlineData(404, """{"detail":"Not found"}""", SpeechToTextFailure.InvalidRequest)]
    public async Task Http_errors_map_to_typed_failures(int status, string body, SpeechToTextFailure expected)
    {
        var (stt, http, log) = Create((_, _) => FakeHttpHandler.Json((HttpStatusCode)status, body));

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => stt.TranscribeAsync(OneSecond, "it", CancellationToken.None));

        Assert.Equal(expected, ex.Reason);
        Assert.Equal(status, ex.StatusCode);
        Assert.Single(http.Requests);
        Assert.False(log.Contains(TestKeys.ApiKey));
    }

    [Fact]
    public async Task Network_error_and_http_timeout_are_typed()
    {
        var network = new ElevenLabsSpeechToText(
            new HttpClient(new FakeHttpHandler((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("DNS")))),
            FakeSettingsStore.WithElevenLabs(), new FakeProtector(), new CollectingLog());
        Assert.Equal(SpeechToTextFailure.Network,
            (await Assert.ThrowsAsync<SpeechToTextException>(() => network.TranscribeAsync(OneSecond, "it", CancellationToken.None))).Reason);

        var slow = new ElevenLabsSpeechToText(
            new HttpClient(new FakeHttpHandler(async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Transcript("mai");
            }))
            { Timeout = TimeSpan.FromMilliseconds(100) },
            FakeSettingsStore.WithElevenLabs(), new FakeProtector(), new CollectingLog());
        Assert.Equal(SpeechToTextFailure.Timeout,
            (await Assert.ThrowsAsync<SpeechToTextException>(() => slow.TranscribeAsync(OneSecond, "it", CancellationToken.None))).Reason);
    }

    [Fact]
    public async Task Short_clip_is_not_sent_and_missing_key_is_not_configured()
    {
        var (stt, http, _) = Create((_, _) => Transcript("x"));
        Assert.Equal("", await stt.TranscribeAsync(new byte[3000], "it", CancellationToken.None));
        Assert.Equal(0, http.Count);

        var (noKey, _, _) = Create((_, _) => Transcript("x"), s => s.Speech.ElevenLabsProtectedApiKey = "");
        Assert.False(noKey.IsConfigured);
        Assert.Equal(SpeechToTextFailure.NotConfigured,
            (await Assert.ThrowsAsync<SpeechToTextException>(() => noKey.TranscribeAsync(OneSecond, "it", CancellationToken.None))).Reason);
    }

    [Fact]
    public async Task Whitespace_and_newlines_in_transcript_are_normalized()
    {
        var (stt, _, _) = Create((_, _) => Transcript("  Ciao\\n\\n  mondo  "));
        Assert.Equal("Ciao mondo", await stt.TranscribeAsync(OneSecond, "it", CancellationToken.None));
    }

    [Fact]
    public async Task Api_key_echoed_by_the_server_never_reaches_log_or_exception()
    {
        var (stt, _, log) = Create((request, _) =>
            FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "proxy error, header xi-api-key=" + request.Header("xi-api-key")));

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => stt.TranscribeAsync(OneSecond, "it", CancellationToken.None));

        Assert.DoesNotContain(TestKeys.ApiKey, ex.Message);
        Assert.True(log.Contains("proxy error"));
        Assert.False(log.Contains(TestKeys.ApiKey));
    }
}
