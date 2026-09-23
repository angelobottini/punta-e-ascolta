using System.Diagnostics;
using System.Net;
using System.Text.Json;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Speech.Cache;
using PuntaEAscolta.Speech.ElevenLabs;
using PuntaEAscolta.Speech.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace PuntaEAscolta.Speech.Tests;

public sealed class SpeechServiceTests(ITestOutputHelper output)
{
    private sealed class Harness : IDisposable
    {
        private readonly TempDirectory _dir = new();

        private Harness(FakeSettingsStore settings, TimeProvider? time, Func<Harness, ISpeechSynthesizer?> cloud)
        {
            Settings = settings;
            Cache = new SpeechCache(_dir.Path, () => 50, Log);
            Service = new SpeechService(cloud(this), Local, Player, Cache, Settings, Log, time ?? TimeProvider.System);
        }

        public CollectingLog Log { get; } = new();
        public FakeSettingsStore Settings { get; }
        public FakePlayer Player { get; } = new();
        public FakeSynthesizer Local { get; } = new("locale");
        public SpeechCache Cache { get; }
        public SpeechService Service { get; }
        public FakeHttpHandler? Http { get; private set; }
        public ElevenLabsSynthesizer? ElevenLabs { get; private set; }

        public static Harness WithElevenLabs(Func<CapturedRequest, int, CancellationToken, Task<HttpResponseMessage>> responder,
            Action<AppSettings>? tweak = null, TimeProvider? time = null) =>
            new(FakeSettingsStore.WithElevenLabs(tweak), time, h =>
            {
                h.Http = new FakeHttpHandler(responder);
                h.ElevenLabs = new ElevenLabsSynthesizer(new HttpClient(h.Http), h.Settings, new FakeProtector(), h.Log);
                return h.ElevenLabs;
            });

        /// <summary>ElevenLabs finto che risponde subito con il PCM "el:testo".</summary>
        public static Harness WithEchoingElevenLabs(Action<AppSettings>? tweak = null, TimeProvider? time = null) =>
            WithElevenLabs((request, _, _) => Task.FromResult(FakeHttpHandler.Audio(TestUtil.Pcm("el:" + BodyText(request)))), tweak, time);

        public static Harness WithCloud(ISpeechSynthesizer? cloud, Action<AppSettings>? tweak = null, TimeProvider? time = null)
        {
            var settings = new AppSettings();
            tweak?.Invoke(settings);
            return new Harness(new FakeSettingsStore(settings), time, _ => cloud);
        }

        public SpeechCacheKey KeyFor(SpeechRequest request) => ElevenLabs!.DescribeForCache(request);

        public string[] PlayedTexts => Player.Played.Select(p => p.Text).ToArray();

        public void Dispose()
        {
            Service.Dispose();
            _dir.Dispose();
        }
    }

    private static string BodyText(CapturedRequest request)
    {
        using var body = JsonDocument.Parse(request.Body!);
        return body.RootElement.GetProperty("text").GetString()!;
    }

    private static SpeechProviderException Fail(SpeechProviderReason reason) => new(reason, "errore finto " + reason);

    // -----------------------------------------------------------------------------------------------------------------
    // Cache
    // -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cloud_audio_is_played_stored_complete_and_then_served_from_cache_within_a_few_ms()
    {
        using var h = Harness.WithEchoingElevenLabs();
        var request = new SpeechRequest("Salva con nome", SpeechKind.Label, "it");

        Assert.Equal(SpeechOutcome.Completed, await h.Service.SpeakWithOutcomeAsync(request, CancellationToken.None).Bounded());
        await h.Service.WaitForBackgroundAsync().Bounded();

        Assert.Equal(["el:Salva con nome"], h.PlayedTexts);
        Assert.Equal(44100, h.Player.Played[0].Format.SampleRate);
        Assert.True(h.Cache.Contains(h.KeyFor(request)));

        long before = Stopwatch.GetTimestamp();
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        double ms = Stopwatch.GetElapsedTime(before, h.Player.Played[1].StartedTimestamp).TotalMilliseconds;
        output.WriteLine($"Hit della cache: riproduzione avviata dopo {ms:F1} ms");

        Assert.Equal(1, h.Http!.Count);
        Assert.Equal("el:Salva con nome", h.Player.Played[1].Text);
        Assert.True(ms < 100, $"hit della cache troppo lento: {ms:F1} ms");
    }

    [Fact]
    public async Task Cache_hit_is_used_even_without_key_but_never_with_windows_provider()
    {
        using var h = Harness.WithEchoingElevenLabs(s => s.Speech.ElevenLabsProtectedApiKey = "");
        var request = new SpeechRequest("Apri", SpeechKind.Label, "it");
        h.Cache.Store(h.KeyFor(request), new PcmFormat(44100), TestUtil.Pcm("dalla-cache"));

        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        Assert.Equal(["dalla-cache"], h.PlayedTexts);

        h.Settings.Current.Speech.Provider = SpeechProviderKind.Windows;
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        Assert.Equal("locale:Apri", h.PlayedTexts[1]);
        Assert.Equal(0, h.Http!.Count);
    }

    [Fact]
    public async Task Disabled_cache_is_neither_read_nor_written()
    {
        using var h = Harness.WithEchoingElevenLabs(s => s.Speech.CacheEnabled = false);
        var request = new SpeechRequest("Chiudi", SpeechKind.Label, "it");

        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        await h.Service.WaitForBackgroundAsync().Bounded();

        Assert.Equal(2, h.Http!.Count);
        Assert.False(h.Cache.Contains(h.KeyFor(request)));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Scelta del fornitore
    // -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task System_messages_are_always_local()
    {
        var cloud = new FakeSynthesizer("cloud");
        using var h = Harness.WithCloud(cloud, s => s.Speech.Provider = SpeechProviderKind.ElevenLabs);

        await h.Service.SpeakAsync(new SpeechRequest("Nessun testo", SpeechKind.System), CancellationToken.None).Bounded();

        Assert.Equal(["locale:Nessun testo"], h.PlayedTexts);
        Assert.Equal(0, cloud.Count);
    }

    [Fact]
    public async Task Sensitive_text_goes_local_only_with_the_privacy_option()
    {
        var cloud = new FakeSynthesizer("cloud");
        using var h = Harness.WithCloud(cloud);
        var sentence = new SpeechRequest("Caro Matteo.", SpeechKind.Sentence, "it", Sensitive: true);

        await h.Service.SpeakAsync(sentence, CancellationToken.None).Bounded();
        h.Settings.Current.Speech.DocumentsWithLocalVoiceOnly = true;
        await h.Service.SpeakAsync(sentence, CancellationToken.None).Bounded();
        await h.Service.SpeakAsync(new SpeechRequest("Salva", SpeechKind.Label), CancellationToken.None).Bounded();

        Assert.Equal(["cloud:Caro Matteo.", "locale:Caro Matteo.", "cloud:Salva"], h.PlayedTexts);
    }

    [Fact]
    public async Task Windows_provider_or_unconfigured_cloud_use_local_voice()
    {
        var cloud = new FakeSynthesizer("cloud");
        using var h = Harness.WithCloud(cloud, s => s.Speech.Provider = SpeechProviderKind.Windows);

        await h.Service.SpeakAsync(new SpeechRequest("Uno", SpeechKind.Label), CancellationToken.None).Bounded();
        h.Settings.Current.Speech.Provider = SpeechProviderKind.Auto;
        cloud.IsConfigured = false;
        await h.Service.SpeakAsync(new SpeechRequest("Due", SpeechKind.Label), CancellationToken.None).Bounded();

        Assert.Equal(["locale:Uno", "locale:Due"], h.PlayedTexts);
        Assert.Equal(0, cloud.Count);

        using var noCloud = Harness.WithCloud(null);
        await noCloud.Service.SpeakAsync(new SpeechRequest("Tre", SpeechKind.Label), CancellationToken.None).Bounded();
        Assert.Equal(["locale:Tre"], noCloud.PlayedTexts);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Tempo massimo per il primo audio e ripiego
    // -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Silent_cloud_falls_back_to_local_right_after_the_label_timeout()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloud = new FakeSynthesizer("cloud")
        {
            Behaviour = async (_, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                throw new InvalidOperationException("mai");
            }
        };
        using var h = Harness.WithCloud(cloud, s => s.Speech.FirstAudioTimeoutLabelMs = 200);

        var watch = Stopwatch.StartNew();
        var outcome = await h.Service.SpeakWithOutcomeAsync(new SpeechRequest("Stampa", SpeechKind.Label), CancellationToken.None).Bounded();
        output.WriteLine($"Ripiego locale dopo {watch.ElapsedMilliseconds} ms (tempo massimo 200 ms)");

        Assert.Equal(SpeechOutcome.Completed, outcome);
        Assert.Equal(["locale:Stampa"], h.PlayedTexts);
        Assert.InRange(watch.ElapsedMilliseconds, 150, 1500);
        await cancelled.Task.Bounded(); // la richiesta cloud abbandonata viene annullata
        Assert.True(h.Log.Contains("entro 200 ms"));
    }

    [Fact]
    public async Task Timeout_counts_until_the_first_pcm_bytes_and_the_late_audio_still_reaches_the_cache()
    {
        var network = new ControlledStream(); // intestazioni subito, byte solo dopo il tempo massimo
        using var h = Harness.WithElevenLabs((_, _, _) => Task.FromResult(FakeHttpHandler.Streaming(network)),
            s => s.Speech.FirstAudioTimeoutSentenceMs = 250);
        var request = new SpeechRequest("Una frase lunga.", SpeechKind.Sentence, "it");

        var watch = Stopwatch.StartNew();
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();

        Assert.Equal(["locale:Una frase lunga."], h.PlayedTexts);
        Assert.InRange(watch.ElapsedMilliseconds, 200, 2000);

        // L'audio già pagato non si butta: la richiesta finisce in sottofondo, senza suonare, e va in cache.
        byte[] late = TestUtil.Repeat(3, 6000);
        network.Push(late);
        network.Complete();
        await h.Service.WaitForBackgroundAsync().Bounded();
        Assert.True(h.Cache.Contains(h.KeyFor(request)));
        Assert.Single(h.Player.Played);
        await TestUtil.WaitUntilAsync(() => network.IsDisposed, what: "connessione chiusa dopo il salvataggio");

        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        Assert.Equal(1, h.Http!.Count);
        Assert.Equal(late, h.Player.Played[^1].Bytes);
    }

    [Fact]
    public async Task Late_cloud_audio_after_a_timeout_does_not_reset_the_circuit_breaker()
    {
        var time = new ManualTimeProvider();
        var networks = new List<ControlledStream>();
        using var h = Harness.WithElevenLabs((_, _, _) =>
        {
            var network = new ControlledStream();
            lock (networks) networks.Add(network);
            return Task.FromResult(FakeHttpHandler.Streaming(network));
        }, s => s.Speech.FirstAudioTimeoutLabelMs = 200, time);

        // Tre letture che scadono: la risposta tardiva in sottofondo non deve far sembrare sano il cloud.
        for (int i = 0; i < 3; i++)
        {
            var speaking = h.Service.SpeakAsync(new SpeechRequest("Etichetta " + i, SpeechKind.Label, "it"), CancellationToken.None);
            await TestUtil.WaitUntilAsync(() => { lock (networks) return networks.Count == i + 1; }, what: "richiesta inviata");
            await TestUtil.WaitUntilAsync(() => time.ActiveTimers >= 2, what: "timer del primo audio e dello stream");
            time.Advance(TimeSpan.FromMilliseconds(200));
            await speaking.Bounded();
            ControlledStream last;
            lock (networks) last = networks[^1];
            last.Push(TestUtil.Repeat(1, 1000));
            last.Complete();
            await h.Service.WaitForBackgroundAsync().Bounded();
        }

        Assert.NotNull(h.Service.CloudSuspendedUntil);
        Assert.Equal(3, h.Http!.Count);
    }

    [Fact]
    public async Task Stop_before_the_first_bytes_lets_the_paid_request_finish_into_the_cache()
    {
        var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = Harness.WithElevenLabs(async (request, _, _) =>
        {
            await respond.Task; // il server risponde dopo lo Stop
            return FakeHttpHandler.Audio(TestUtil.Pcm("el:" + BodyText(request)));
        });
        var request = new SpeechRequest("Salva con nome", SpeechKind.Label, "it");

        var speaking = h.Service.SpeakWithOutcomeAsync(request, CancellationToken.None);
        await TestUtil.WaitUntilAsync(() => h.Http!.Count == 1, what: "richiesta inviata");
        var watch = Stopwatch.StartNew();
        h.Service.Stop();
        Assert.Equal(SpeechOutcome.Stopped, await speaking.Bounded());
        output.WriteLine($"Stop prima dei primi byte: SpeakAsync terminata dopo {watch.ElapsedMilliseconds} ms");
        Assert.False(h.Service.IsSpeaking);

        respond.TrySetResult();
        await h.Service.WaitForBackgroundAsync().Bounded();

        Assert.Empty(h.Player.Played); // niente di udibile
        Assert.True(h.Cache.Contains(h.KeyFor(request)));
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        Assert.Equal(1, h.Http!.Count);
        Assert.Equal(["el:Salva con nome"], h.PlayedTexts);
    }

    [Fact]
    public async Task Abandoned_request_that_never_sends_audio_is_dropped_after_the_background_limit()
    {
        var time = new ManualTimeProvider();
        var network = new ControlledStream(); // intestazioni subito, byte mai
        using var h = Harness.WithElevenLabs((_, _, _) => Task.FromResult(FakeHttpHandler.Streaming(network)), time: time);
        var request = new SpeechRequest("Frase mai arrivata.", SpeechKind.Sentence, "it");

        var speaking = h.Service.SpeakWithOutcomeAsync(request, CancellationToken.None);
        await TestUtil.WaitUntilAsync(() => h.Http!.Count == 1, what: "richiesta inviata");
        h.Service.Stop();
        Assert.Equal(SpeechOutcome.Stopped, await speaking.Bounded());

        await TestUtil.WaitUntilAsync(() => time.ActiveTimers >= 2, what: "timer del sottofondo e dello stream");
        time.Advance(TimeSpan.FromSeconds(16));
        await h.Service.WaitForBackgroundAsync().Bounded();

        Assert.False(h.Cache.Contains(h.KeyFor(request)));
        Assert.Empty(h.Player.Played);
        await TestUtil.WaitUntilAsync(() => network.IsDisposed, what: "connessione chiusa");
    }

    [Fact]
    public async Task Without_cache_an_abandoned_request_is_still_cancelled()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = Harness.WithElevenLabs(async (_, _, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException("mai");
        }, s => s.Speech.CacheEnabled = false);

        var speaking = h.Service.SpeakWithOutcomeAsync(new SpeechRequest("Apri", SpeechKind.Label, "it"), CancellationToken.None);
        await TestUtil.WaitUntilAsync(() => h.Http!.Count == 1, what: "richiesta inviata");
        h.Service.Stop();

        Assert.Equal(SpeechOutcome.Stopped, await speaking.Bounded());
        await cancelled.Task.Bounded();
    }

    [Fact]
    public async Task Provider_errors_fall_back_to_local_for_that_reading()
    {
        var cloud = new FakeSynthesizer("cloud") { Behaviour = (_, _) => throw Fail(SpeechProviderReason.RateLimited) };
        using var h = Harness.WithCloud(cloud);

        for (int i = 0; i < 4; i++)
        {
            await h.Service.SpeakAsync(new SpeechRequest("Taglia", SpeechKind.Label), CancellationToken.None).Bounded();
        }

        Assert.All(h.PlayedTexts, t => Assert.Equal("locale:Taglia", t));
        Assert.Equal(4, cloud.Count); // RateLimited non sospende il cloud
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Interruttore automatico
    // -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Three_network_failures_suspend_cloud_60s_then_a_failed_probe_suspends_5_minutes()
    {
        var time = new ManualTimeProvider();
        var cloud = new FakeSynthesizer("cloud") { Behaviour = (_, _) => throw Fail(SpeechProviderReason.Network) };
        using var h = Harness.WithCloud(cloud, time: time);
        async Task Speak() => await h.Service.SpeakAsync(new SpeechRequest("Copia", SpeechKind.Label), CancellationToken.None).Bounded();

        for (int i = 0; i < 3; i++) await Speak();
        Assert.Equal(3, cloud.Count);

        await Speak();
        Assert.Equal(3, cloud.Count); // sospeso: nessun tentativo in linea
        Assert.NotNull(h.Service.CloudSuspendedUntil);

        time.Advance(TimeSpan.FromSeconds(60));
        await Speak();
        Assert.Equal(4, cloud.Count); // prova dopo la sospensione, fallita

        time.Advance(TimeSpan.FromSeconds(61));
        await Speak();
        Assert.Equal(4, cloud.Count); // ora 5 minuti

        time.Advance(TimeSpan.FromMinutes(4));
        cloud.Behaviour = (r, _) => Task.FromResult(new SpeechAudio(new MemoryStream(TestUtil.Pcm("cloud:" + r.Text)), new PcmFormat(22050)));
        await Speak();
        Assert.Equal(5, cloud.Count);
        Assert.Null(h.Service.CloudSuspendedUntil);
        Assert.Equal("cloud:Copia", h.PlayedTexts[^1]);
        Assert.Equal(6, h.PlayedTexts.Count(t => t == "locale:Copia"));
    }

    [Fact]
    public async Task Invalid_key_suspends_cloud_until_settings_change()
    {
        using var h = Harness.WithElevenLabs((_, _, _) =>
            Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.Unauthorized, """{"detail":{"code":"invalid_api_key","message":"Invalid API key"}}""")));
        var request = new SpeechRequest("Incolla", SpeechKind.Label, "it");

        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        Assert.Equal(1, h.Http!.Count);
        Assert.Equal(DateTimeOffset.MaxValue, h.Service.CloudSuspendedUntil);

        h.Settings.Save(h.Settings.Current);
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        Assert.Equal(2, h.Http.Count);
        Assert.Equal(["locale:Incolla", "locale:Incolla", "locale:Incolla"], h.PlayedTexts);
        Assert.False(h.Log.Contains(TestKeys.ApiKey));
    }

    [Fact]
    public async Task Key_without_text_to_speech_permission_suspends_cloud_until_settings_change_and_says_why()
    {
        // Chiave valida ma limitata (prove dal vivo del 23/09/2026): 401 missing_permissions. Il server ripete la chiave nel
        // messaggio: non deve arrivare nel registro.
        using var h = Harness.WithElevenLabs((_, _, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.Unauthorized,
            "{\"detail\":{\"status\":\"missing_permissions\",\"message\":\"The API key you used (" + TestKeys.ApiKey +
            ") is missing the permission text_to_speech to execute this operation.\"}}")));
        var request = new SpeechRequest("Incolla", SpeechKind.Label, "it");

        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();

        Assert.Equal(1, h.Http!.Count); // come una chiave rifiutata: nessun altro tentativo in linea
        Assert.Equal(DateTimeOffset.MaxValue, h.Service.CloudSuspendedUntil);
        Assert.Equal(["locale:Incolla", "locale:Incolla"], h.PlayedTexts);
        Assert.Contains(h.Log.Entries, e => e.StartsWith("WARN ", StringComparison.Ordinal) && e.Contains("non ha il permesso text_to_speech", StringComparison.Ordinal));
        Assert.False(h.Log.Contains(TestKeys.ApiKey));

        h.Settings.Save(h.Settings.Current);
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        Assert.Equal(2, h.Http.Count);
    }

    [Fact]
    public async Task Quota_exceeded_suspends_cloud_for_30_minutes()
    {
        var time = new ManualTimeProvider();
        using var h = Harness.WithElevenLabs((_, _, _) =>
            Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.PaymentRequired, """{"detail":{"code":"insufficient_credits","message":"no credits"}}""")),
            time: time);
        var request = new SpeechRequest("Esporta", SpeechKind.Label, "en");

        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        time.Advance(TimeSpan.FromMinutes(29));
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        Assert.Equal(1, h.Http!.Count);

        time.Advance(TimeSpan.FromMinutes(1));
        await h.Service.SpeakAsync(request, CancellationToken.None).Bounded();
        Assert.Equal(2, h.Http.Count);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Pezzi e preparazione anticipata
    // -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Chunks_play_in_order_preparing_only_the_next_one()
    {
        var cloud = new FakeSynthesizer("cloud");
        using var h = Harness.WithCloud(cloud);
        var requestedAtPlayStart = new List<int>();
        h.Player.ReadDelayMs = 20;
        h.Player.OnPlayStarted = _ => requestedAtPlayStart.Add(cloud.Count);
        var request = new SpeechRequest("Uno. Due. Tre. Quattro.", SpeechKind.Sentence, "it")
        {
            Chunks = ["Uno.", "Due.", " ", "Tre.", "Quattro."]
        };

        var outcome = await h.Service.SpeakWithOutcomeAsync(request, CancellationToken.None).Bounded();

        Assert.Equal(SpeechOutcome.Completed, outcome);
        Assert.Equal(["cloud:Uno.", "cloud:Due.", "cloud:Tre.", "cloud:Quattro."], h.PlayedTexts);
        Assert.Equal(["Uno.", "Due.", "Tre.", "Quattro."], cloud.Requests.Select(r => r.Text));
        Assert.Equal([2, 3, 4, 4], requestedAtPlayStart);
        Assert.All(cloud.Requests, r => Assert.Equal(SpeechKind.Sentence, r.Kind));
    }

    [Fact]
    public async Task Stop_during_chunks_does_not_request_the_rest()
    {
        var cloud = new FakeSynthesizer("cloud");
        using var h = Harness.WithCloud(cloud);
        h.Player.ReadSize = 2;
        h.Player.ReadDelayMs = 30;
        var request = new SpeechRequest("x", SpeechKind.Sentence) { Chunks = ["Primo pezzo.", "Secondo.", "Terzo.", "Quarto."] };

        var speaking = h.Service.SpeakWithOutcomeAsync(request, CancellationToken.None);
        await TestUtil.WaitUntilAsync(() => h.Player.Played.Count == 1 && h.Player.Played[0].Length > 0, what: "primo pezzo in riproduzione");
        h.Service.Stop();

        Assert.Equal(SpeechOutcome.Stopped, await speaking.Bounded());
        Assert.Equal(2, cloud.Count); // il pezzo corrente e al massimo uno preparato in anticipo
        Assert.Single(h.Player.Played);
    }

    [Fact]
    public async Task Stop_during_first_chunk_puts_the_prefetched_second_chunk_in_cache()
    {
        var network = new ControlledStream(); // pezzo 1: primi byte, poi la rete si ferma
        using var h = Harness.WithElevenLabs((request, _, _) => Task.FromResult(BodyText(request) == "Primo pezzo."
            ? FakeHttpHandler.Streaming(network)
            : FakeHttpHandler.Audio(TestUtil.Pcm("el:" + BodyText(request)))));
        var request = new SpeechRequest("Primo pezzo. Secondo pezzo.", SpeechKind.Sentence, "it") { Chunks = ["Primo pezzo.", "Secondo pezzo."] };
        var second = new SpeechRequest("Secondo pezzo.", SpeechKind.Sentence, "it");

        network.Push(TestUtil.Repeat(1, 8000));
        var speaking = h.Service.SpeakWithOutcomeAsync(request, CancellationToken.None);
        await TestUtil.WaitUntilAsync(() => h.Http!.Count == 2 && h.Player.Played.Count == 1 && h.Player.Played[0].Length == 8000,
            what: "pezzo 1 in riproduzione e pezzo 2 richiesto");

        var watch = Stopwatch.StartNew();
        h.Service.Stop();
        Assert.False(h.Service.IsSpeaking);
        Assert.Equal(SpeechOutcome.Stopped, await speaking.Bounded());
        output.WriteLine($"Stop durante il pezzo 1: SpeakAsync terminata dopo {watch.ElapsedMilliseconds} ms");
        Assert.Single(h.Player.Played); // il pezzo 2 non si sente

        network.Complete(); // anche il pezzo 1 finisce in sottofondo (comportamento già esistente)
        await h.Service.WaitForBackgroundAsync().Bounded();

        Assert.True(h.Cache.Contains(h.KeyFor(second)));
        await h.Service.SpeakAsync(second, CancellationToken.None).Bounded();
        Assert.Equal(2, h.Http!.Count); // rilettura del pezzo 2: nessuna chiamata HTTP
        Assert.Equal("el:Secondo pezzo.", h.PlayedTexts[^1]);
    }

    [Fact]
    public async Task Failed_chunk_is_skipped_and_the_rest_is_read()
    {
        using var h = Harness.WithCloud(null);
        h.Local.Behaviour = (r, _) => r.Text == "Due."
            ? throw new InvalidOperationException("voce rotta")
            : Task.FromResult(new SpeechAudio(new MemoryStream(TestUtil.Pcm("locale:" + r.Text)), new PcmFormat(22050)));

        var outcome = await h.Service.SpeakWithOutcomeAsync(new SpeechRequest("x", SpeechKind.Sentence) { Chunks = ["Uno.", "Due.", "Tre."] },
            CancellationToken.None).Bounded();

        Assert.Equal(SpeechOutcome.Completed, outcome);
        Assert.Equal(["locale:Uno.", "locale:Tre."], h.PlayedTexts);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Stop a metà stream e scaricamento in sottofondo
    // -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_mid_stream_finishes_download_in_background_and_caches_the_complete_audio()
    {
        var network = new ControlledStream();
        using var h = Harness.WithElevenLabs((_, _, _) => Task.FromResult(FakeHttpHandler.Streaming(network)));
        var request = new SpeechRequest("Una frase da scaricare.", SpeechKind.Sentence, "it");
        var events = new List<bool>();
        h.Service.SpeakingChanged += events.Add;
        byte[] first = TestUtil.Repeat(1, 8000), rest = TestUtil.Repeat(2, 6000);

        network.Push(first);
        var speaking = h.Service.SpeakWithOutcomeAsync(request, CancellationToken.None);
        await TestUtil.WaitUntilAsync(() => h.Player.Played.Count == 1 && h.Player.Played[0].Length == first.Length, what: "primi byte suonati");

        h.Service.Stop();
        Assert.False(h.Service.IsSpeaking);
        Assert.Equal([true, false], events);
        Assert.Equal(SpeechOutcome.Stopped, await speaking.Bounded());
        Assert.False(h.Cache.Contains(h.KeyFor(request))); // mai audio parziale

        network.Push(rest);
        network.Complete();
        await h.Service.WaitForBackgroundAsync().Bounded();

        Assert.True(h.Cache.TryOpen(h.KeyFor(request), out var cached, out var format));
        using (cached)
        using (var all = new MemoryStream())
        {
            await cached.CopyToAsync(all);
            Assert.Equal(first.Concat(rest).ToArray(), all.ToArray());
        }
        Assert.Equal(44100, format.SampleRate);
        Assert.Equal([true, false], events);
    }

    [Fact]
    public async Task Stop_mid_stream_discards_audio_when_download_does_not_finish()
    {
        var time = new ManualTimeProvider();
        var network = new ControlledStream();
        using var h = Harness.WithElevenLabs((_, _, _) => Task.FromResult(FakeHttpHandler.Streaming(network)), time: time);
        var request = new SpeechRequest("Frase lenta.", SpeechKind.Sentence, "it");

        network.Push(TestUtil.Repeat(1, 8000));
        var speaking = h.Service.SpeakAsync(request, CancellationToken.None);
        await TestUtil.WaitUntilAsync(() => h.Player.Played.Count == 1 && h.Player.Played[0].Length == 8000, what: "primi byte suonati");
        h.Service.Stop();
        await speaking.Bounded();

        await TestUtil.WaitUntilAsync(() => time.ActiveTimers > 0, what: "timer dello scaricamento");
        time.Advance(TimeSpan.FromSeconds(16));
        await h.Service.WaitForBackgroundAsync().Bounded();

        Assert.False(h.Cache.Contains(h.KeyFor(request)));
        await TestUtil.WaitUntilAsync(() => network.IsDisposed, what: "connessione chiusa");
    }

    [Fact]
    public async Task Network_failure_mid_stream_ends_the_chunk_and_is_not_cached()
    {
        var network = new ControlledStream();
        using var h = Harness.WithElevenLabs((_, _, _) => Task.FromResult(FakeHttpHandler.Streaming(network)));
        var request = new SpeechRequest("Frase interrotta.", SpeechKind.Sentence, "it");

        network.Push(TestUtil.Repeat(1, 4000));
        var speaking = h.Service.SpeakWithOutcomeAsync(request, CancellationToken.None);
        await TestUtil.WaitUntilAsync(() => h.Player.Played.Count == 1 && h.Player.Played[0].Length == 4000, what: "primi byte suonati");
        network.Fail(new IOException("connessione persa"));

        Assert.Equal(SpeechOutcome.Completed, await speaking.Bounded());
        await h.Service.WaitForBackgroundAsync().Bounded();
        Assert.True(h.Player.Played[0].Completed);
        Assert.Equal(4000, h.Player.Played[0].Length);
        Assert.False(h.Cache.Contains(h.KeyFor(request)));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Stop, sostituzione, eventi, volume
    // -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task New_speak_supersedes_the_previous_without_flicker()
    {
        using var h = Harness.WithCloud(null);
        h.Player.ReadSize = 2;
        h.Player.ReadDelayMs = 20;
        var events = new List<bool>();
        h.Service.SpeakingChanged += events.Add;

        var first = h.Service.SpeakWithOutcomeAsync(new SpeechRequest("Prima lettura lunga", SpeechKind.Sentence), CancellationToken.None);
        await TestUtil.WaitUntilAsync(() => h.Player.Played.Count == 1, what: "prima lettura avviata");
        var second = h.Service.SpeakWithOutcomeAsync(new SpeechRequest("Seconda", SpeechKind.Label), CancellationToken.None);

        Assert.Equal(SpeechOutcome.Superseded, await first.Bounded());
        Assert.Equal(SpeechOutcome.Completed, await second.Bounded());
        Assert.Equal("locale:Seconda", h.PlayedTexts[^1]);
        Assert.False(h.Player.Played[0].Completed);
        Assert.Equal([true, false], events);
        Assert.False(h.Service.IsSpeaking);
    }

    [Fact]
    public async Task Stop_is_immediate_and_idempotent()
    {
        using var h = Harness.WithCloud(null);
        var events = new List<bool>();
        h.Service.SpeakingChanged += events.Add;

        h.Service.Stop();
        h.Service.Stop();
        Assert.Empty(events);

        h.Player.ReadSize = 2;
        h.Player.ReadDelayMs = 50;
        var speaking = h.Service.SpeakWithOutcomeAsync(new SpeechRequest("Testo abbastanza lungo", SpeechKind.Sentence), CancellationToken.None);
        await TestUtil.WaitUntilAsync(() => h.Player.IsPlaying, what: "riproduzione");
        Assert.True(h.Service.IsSpeaking);

        var watch = Stopwatch.StartNew();
        h.Service.Stop();
        Assert.False(h.Service.IsSpeaking);
        Assert.Equal(SpeechOutcome.Stopped, await speaking.Bounded());
        output.WriteLine($"Stop: SpeakAsync terminata dopo {watch.ElapsedMilliseconds} ms");
        h.Service.Stop();

        Assert.Equal([true, false], events);
        Assert.True(h.Player.StopCount >= 3);
    }

    [Fact]
    public async Task Volume_comes_from_settings_and_is_clamped()
    {
        using var h = Harness.WithCloud(null, s => s.Speech.Volume = 0.15);
        await h.Service.SpeakAsync(new SpeechRequest("Piano", SpeechKind.Label), CancellationToken.None).Bounded();
        h.Settings.Current.Speech.Volume = 7;
        await h.Service.SpeakAsync(new SpeechRequest("Forte", SpeechKind.Label), CancellationToken.None).Bounded();
        h.Settings.Current.Speech.Volume = double.NaN;
        await h.Service.SpeakAsync(new SpeechRequest("Strano", SpeechKind.Label), CancellationToken.None).Bounded();

        Assert.Equal([0.15, 1.0, 1.0], h.Player.Played.Select(p => p.Volume));
    }

    [Fact]
    public async Task Failures_never_escape_except_caller_cancellation()
    {
        using var h = Harness.WithCloud(null);
        h.Local.Behaviour = (_, _) => throw new InvalidOperationException("voce rotta");
        Assert.Equal(SpeechOutcome.Failed, await h.Service.SpeakWithOutcomeAsync(new SpeechRequest("a", SpeechKind.Label), CancellationToken.None).Bounded());

        using var h2 = Harness.WithCloud(null);
        h2.Player.ThrowOnPlay = new InvalidOperationException("dispositivo audio assente");
        await h2.Service.SpeakAsync(new SpeechRequest("b", SpeechKind.Label), CancellationToken.None).Bounded();
        Assert.True(h2.Log.Contains("riproduzione non riuscita"));

        using var h3 = Harness.WithCloud(null);
        h3.Player.ReadSize = 2;
        h3.Player.ReadDelayMs = 50;
        using var cts = new CancellationTokenSource();
        var speaking = h3.Service.SpeakAsync(new SpeechRequest("Testo lungo da annullare", SpeechKind.Sentence), cts.Token);
        await TestUtil.WaitUntilAsync(() => h3.Player.IsPlaying, what: "riproduzione");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => speaking.Bounded());
        Assert.False(h3.Service.IsSpeaking);
    }

    [Fact]
    public async Task Empty_request_plays_nothing_and_disposed_service_is_silent()
    {
        using var h = Harness.WithCloud(null);
        var events = new List<bool>();
        h.Service.SpeakingChanged += events.Add;

        Assert.Equal(SpeechOutcome.Completed, await h.Service.SpeakWithOutcomeAsync(new SpeechRequest("  ", SpeechKind.Label), CancellationToken.None));
        Assert.Equal(SpeechOutcome.Completed, await h.Service.SpeakWithOutcomeAsync(new SpeechRequest("", SpeechKind.Sentence) { Chunks = ["", " "] }, CancellationToken.None));
        h.Service.Dispose();
        Assert.Equal(SpeechOutcome.Stopped, await h.Service.SpeakWithOutcomeAsync(new SpeechRequest("dopo", SpeechKind.Label), CancellationToken.None));

        Assert.Empty(h.Player.Played);
        Assert.Empty(events);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Riservatezza dei log
    // -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Api_key_never_reaches_the_log_on_any_path()
    {
        int call = 0;
        using var h = Harness.WithElevenLabs((request, _, _) =>
        {
            int n = Interlocked.Increment(ref call);
            return n switch
            {
                1 => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.Forbidden, """{"detail":{"code":"invalid_output_format","message":"pcm_44100 requires Pro"}}""")),
                2 => Task.FromResult(FakeHttpHandler.Audio(TestUtil.Pcm("ok"))),
                3 => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "boom " + request.Header("xi-api-key"))),
                4 => Task.FromException<HttpResponseMessage>(new HttpRequestException("rete assente")),
                _ => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.Unauthorized, """{"detail":{"code":"invalid_api_key","message":"bad"}}"""))
            };
        });

        foreach (string text in new[] { "uno", "due", "tre", "quattro" })
        {
            await h.Service.SpeakAsync(new SpeechRequest(text, SpeechKind.Label, "it"), CancellationToken.None).Bounded();
        }
        h.Settings.Save(h.Settings.Current);
        await h.Service.SpeakAsync(new SpeechRequest("cinque", SpeechKind.Label, "it"), CancellationToken.None).Bounded();

        Assert.True(h.Http!.Count >= 5);
        Assert.All(h.Http.Requests, r => Assert.Equal(TestKeys.ApiKey, r.Header("xi-api-key")));
        Assert.NotEmpty(h.Log.Entries);
        Assert.True(h.Log.Contains("boom")); // il 500 è stato registrato...
        // ...ma anche se il server rimanda la chiave nel corpo dell'errore, nel log non arriva mai.
        Assert.False(h.Log.Contains(TestKeys.ApiKey));
    }

    [Fact]
    public async Task Spoken_text_is_logged_only_at_debug_level()
    {
        using var h = Harness.WithEchoingElevenLabs();
        h.Log.IsDebugEnabled = false;
        await h.Service.SpeakAsync(new SpeechRequest("TestoRiservatissimo", SpeechKind.Sentence, "it"), CancellationToken.None).Bounded();
        await h.Service.SpeakAsync(new SpeechRequest("AltroTestoRiservato", SpeechKind.System), CancellationToken.None).Bounded();
        Assert.False(h.Log.Contains("Riservat"));

        h.Log.IsDebugEnabled = true;
        await h.Service.SpeakAsync(new SpeechRequest("TestoDiDiagnosi", SpeechKind.Label, "it"), CancellationToken.None).Bounded();
        Assert.Contains(h.Log.Entries, e => e.StartsWith("DEBUG", StringComparison.Ordinal) && e.Contains("TestoDiDiagnosi", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Log.Entries, e => !e.StartsWith("DEBUG", StringComparison.Ordinal) && e.Contains("TestoDiDiagnosi", StringComparison.Ordinal));
    }
}
