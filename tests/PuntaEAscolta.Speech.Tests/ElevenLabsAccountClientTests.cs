using System.Net;
using PuntaEAscolta.Speech.ElevenLabs;
using PuntaEAscolta.Speech.Tests.Fakes;
using Xunit;

namespace PuntaEAscolta.Speech.Tests;

public sealed class ElevenLabsAccountClientTests
{
    [Fact]
    public async Task Voices_are_paged_on_v2_deduplicated_and_sorted()
    {
        var http = new FakeHttpHandler((request, _) => request.Uri.Query.Contains("next_page_token=p2")
            ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"voices":[{"voice_id":"b","name":"Bianca","labels":{"gender":"female","accent":"italian"}},{"voice_id":"a","name":"Aldo"}],"has_more":false}""")
            : FakeHttpHandler.Json(HttpStatusCode.OK, """{"voices":[{"voice_id":"c","name":"Carlo","category":"premade"},{"voice_id":"a","name":"Aldo"}],"has_more":true,"next_page_token":"p2"}"""));
        var client = new ElevenLabsAccountClient(new HttpClient(http));

        var voices = await client.GetVoicesAsync(TestKeys.ApiKey, CancellationToken.None);

        Assert.Equal(["Aldo", "Bianca", "Carlo"], voices.Select(v => v.Name));
        Assert.Equal("Bianca (female, italian)", voices[1].DisplayName);
        Assert.All(http.Requests, r => Assert.Equal(TestKeys.ApiKey, r.Header("xi-api-key")));
        Assert.StartsWith("https://api.elevenlabs.io/v2/voices?page_size=100", http.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task Voices_fall_back_to_v1_when_v2_fails()
    {
        var http = new FakeHttpHandler((request, _) => request.Uri.AbsolutePath == "/v2/voices"
            ? FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"detail":"Not found"}""")
            : FakeHttpHandler.Json(HttpStatusCode.OK, """{"voices":[{"voice_id":"x","name":"Xenia"}]}"""));

        var voices = await new ElevenLabsAccountClient(new HttpClient(http)).GetVoicesAsync(TestKeys.ApiKey, CancellationToken.None);

        Assert.Equal("Xenia", Assert.Single(voices).Name);
        Assert.Equal("/v1/voices", http.Requests[^1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Subscription_and_key_validation()
    {
        var http = new FakeHttpHandler((request, _) => request.Header("xi-api-key") == TestKeys.ApiKey
            ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"tier":"pro","status":"active","character_count":1000,"character_limit":600000,"next_character_count_reset_unix":1790000000}""")
            : FakeHttpHandler.Json(HttpStatusCode.Unauthorized, """{"detail":{"code":"invalid_api_key","message":"bad"}}"""));
        var client = new ElevenLabsAccountClient(new HttpClient(http));

        var subscription = await client.GetSubscriptionAsync(TestKeys.ApiKey, CancellationToken.None);
        Assert.Equal("pro", subscription.Tier);
        Assert.Equal(599000, subscription.CharactersRemaining);
        Assert.NotNull(subscription.NextReset);

        Assert.True(await client.ValidateKeyAsync(TestKeys.ApiKey, CancellationToken.None));
        Assert.False(await client.ValidateKeyAsync("sk_sbagliata", CancellationToken.None));
        Assert.False(await client.ValidateKeyAsync("  ", CancellationToken.None));
    }

    [Fact]
    public async Task Network_problems_are_not_reported_as_invalid_key()
    {
        var client = new ElevenLabsAccountClient(new HttpClient(new FakeHttpHandler((_, _, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("rete assente")))));

        var ex = await Assert.ThrowsAsync<SpeechProviderException>(() => client.ValidateKeyAsync(TestKeys.ApiKey, CancellationToken.None));
        Assert.Equal(SpeechProviderReason.Network, ex.Reason);
    }

    [Fact]
    public async Task Models_are_filtered_to_text_to_speech()
    {
        var http = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """
            [{"model_id":"eleven_flash_v2_5","name":"Flash v2.5","can_do_text_to_speech":true,"languages":[{"language_id":"it"},{"language_id":"en"}],"model_rates":{"character_cost_multiplier":0.5}},
             {"model_id":"eleven_english_sts_v2","name":"STS","can_do_text_to_speech":false}]
            """));

        var models = await new ElevenLabsAccountClient(new HttpClient(http)).GetModelsAsync(TestKeys.ApiKey, CancellationToken.None);

        var flash = Assert.Single(models);
        Assert.True(flash.SupportsItalian);
        Assert.True(flash.SupportsLanguageCode);
        Assert.Equal(0.5, flash.CharacterCostMultiplier);
    }
}
