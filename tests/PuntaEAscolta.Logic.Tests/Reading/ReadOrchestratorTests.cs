using System.Diagnostics;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Reading;
using PuntaEAscolta.Logic.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace PuntaEAscolta.Logic.Tests.Reading;

public sealed class ReadOrchestratorTests : IDisposable
{
    private static readonly ScreenPoint P = new(400, 300);

    private readonly FakeResolver _resolver = new();
    private readonly FakeSpeech _speech = new();
    private readonly FakeSettingsStore _settings = new();
    private readonly ListLog _log = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<IDisposable> _toDispose = new();
    private readonly ITestOutputHelper _output;

    public ReadOrchestratorTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var d in _toDispose) d.Dispose();
    }

    private ReadOrchestrator Create(IDictationService? dictation = null)
    {
        var o = new ReadOrchestrator(_resolver, _speech, dictation, _settings, _log, _time);
        _toDispose.Add(o);
        return o;
    }

    private static TriggerButtonEvent Down(long t, ScreenPoint? p = null) => new(true, p ?? P, t);

    private static TriggerButtonEvent Up(long t, ScreenPoint? p = null) => new(false, p ?? P, t);

    private static HotkeyEvent Hotkey(HotkeyAction action, long t) => new(action, P, t);

    private static async Task Idle(ReadOrchestrator o) => await o.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task Flush(ReadOrchestrator o) => await o.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

    // ---------------------------------------------------------------------------------------------
    // Lettura di base
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClickDown_ReadsAtPointer_AndSpeaksSentencesAsChunks()
    {
        _resolver.Handler = (_, _) => Task.FromResult(FakeResolver.Text("Prima frase. Seconda frase!", SpeechKind.Sentence, "it", sensitive: true));
        var o = Create();
        var outcomes = new List<ReadOutcome>();
        o.OutcomeProduced += outcomes.Add;

        o.HandleInput(Down(1000));
        o.HandleInput(Up(1080));
        await Idle(o);

        var call = Assert.Single(_resolver.Calls);
        Assert.Equal(new ReadRequest(ReadRequestKind.AtPointer, P), call.Request);
        var spoken = Assert.Single(_speech.Requests);
        Assert.Equal(SpeechKind.Sentence, spoken.Kind);
        Assert.Equal("it", spoken.LanguageHint);
        Assert.True(spoken.Sensitive);
        Assert.Equal(new[] { "Prima frase.", "Seconda frase!" }, spoken.Chunks);
        Assert.Single(outcomes);
    }

    [Fact]
    public async Task LabelOutcome_KeepsKindAndLanguage()
    {
        _resolver.Handler = (_, _) => Task.FromResult(FakeResolver.Text("Save As", SpeechKind.Label, "en"));
        var o = Create();

        o.HandleInput(Down(1000));
        await Idle(o);

        var spoken = Assert.Single(_speech.Requests);
        Assert.Equal("Save As", spoken.Text);
        Assert.Equal(SpeechKind.Label, spoken.Kind);
        Assert.Equal("en", spoken.LanguageHint);
        Assert.Equal(new[] { "Save As" }, spoken.Chunks);
    }

    [Fact]
    public async Task NothingFound_SpeaksNothingFoundTextAsSystem()
    {
        _resolver.Handler = (_, _) => Task.FromResult(ReadOutcome.Nothing(5, "vuoto"));
        _settings.Current.Reading.NothingFoundText = "Niente qui";
        var o = Create();

        o.HandleInput(Down(1000));
        await Idle(o);

        var spoken = Assert.Single(_speech.Requests);
        Assert.Equal("Niente qui", spoken.Text);
        Assert.Equal(SpeechKind.System, spoken.Kind);
    }

    [Fact]
    public async Task NothingFound_Silent_WhenDisabled()
    {
        _resolver.Handler = (_, _) => Task.FromResult(ReadOutcome.Nothing(5, "vuoto"));
        _settings.Current.Reading.SpeakWhenNothingFound = false;
        var o = Create();
        var outcomes = 0;
        o.OutcomeProduced += _ => outcomes++;

        o.HandleInput(Down(1000));
        await Idle(o);

        Assert.Empty(_speech.Requests);
        Assert.Equal(1, outcomes);
    }

    [Fact]
    public async Task ResolverThrows_NoCrash_AndNextClickWorks()
    {
        int n = 0;
        _resolver.Handler = (_, _) => ++n == 1 ? throw new InvalidOperationException("guasto") : Task.FromResult(FakeResolver.Text("Ok."));
        var o = Create();

        o.HandleInput(Down(1000));
        await Idle(o);
        o.HandleInput(Down(3000));
        await Idle(o);

        Assert.Equal(2, _resolver.Calls.Count);
        Assert.Equal("Ok.", Assert.Single(_speech.Requests).Text);
        Assert.Contains(_log.Entries, e => e.Level == "ERROR");
    }

    [Fact]
    public async Task SpeechStoppedByOthers_IsNotLoggedAsError()
    {
        _speech.ThrowOnSpeak = new OperationCanceledException("fermata con Esc");
        var o = Create();

        o.HandleInput(Down(1000));
        await Idle(o);

        Assert.Single(_speech.Requests);
        Assert.DoesNotContain(_log.Entries, e => e.Level == "ERROR");
    }

    [Fact]
    public async Task ClickToSpeechLatency_WithInstantFakes_IsSmall()
    {
        var o = Create();
        var samples = new List<double>();

        for (int i = 0; i < 40; i++)
        {
            var sw = Stopwatch.StartNew();
            o.HandleInput(Down(10_000 + i * 1000L));
            Assert.True(await _speech.WaitStartedAsync());
            samples.Add(sw.Elapsed.TotalMilliseconds);
            await Idle(o);
        }

        samples.Sort();
        double median = samples[samples.Count / 2];
        _output.WriteLine($"Latenza HandleInput -> SpeakAsync con finti immediati: mediana {median:0.000} ms, massimo {samples[^1]:0.000} ms");
        Assert.True(median < 50, $"mediana {median:0.0} ms");
    }

    // ---------------------------------------------------------------------------------------------
    // Gesti
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Debounce_IgnoresClicksTooCloseToThePreviousActivation()
    {
        _settings.Current.Input.DebounceMs = 350;
        var o = Create();

        o.HandleInput(Down(1000));
        await Idle(o);
        o.HandleInput(Down(1200)); // rimbalzo
        await Idle(o);
        Assert.Single(_resolver.Calls);

        o.HandleInput(Down(1400)); // 400 ms dalla prima attivazione
        await Idle(o);
        Assert.Equal(2, _resolver.Calls.Count);
    }

    [Fact]
    public async Task LongPressEnabled_ShortClickDecidedOnUp()
    {
        _settings.Current.Input.LongPressEnabled = true;
        _settings.Current.Input.LongPressMs = 700;
        var o = Create();

        o.HandleInput(Down(1000));
        await Flush(o);
        Assert.Empty(_resolver.Calls);
        Assert.Equal(1, _time.ActiveTimers);

        o.HandleInput(Up(1150));
        await Idle(o);

        Assert.Equal(ReadRequestKind.AtPointer, Assert.Single(_resolver.Calls).Request.Kind);
        Assert.Equal(0, _time.ActiveTimers);
        _time.Advance(TimeSpan.FromSeconds(2));
        await Idle(o);
        Assert.Single(_resolver.Calls);
    }

    [Fact]
    public async Task LongPressEnabled_TimerTriggersZoneRead_UpDoesNothing()
    {
        _settings.Current.Input.LongPressEnabled = true;
        _settings.Current.Input.LongPressMs = 700;
        var o = Create();
        var down = new ScreenPoint(10, 20);

        o.HandleInput(Down(1000, down));
        await Flush(o);
        _time.Advance(TimeSpan.FromMilliseconds(699));
        await Flush(o);
        Assert.Empty(_resolver.Calls);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await Flush(o);
        await Idle(o);
        var call = Assert.Single(_resolver.Calls);
        Assert.Equal(ReadRequestKind.ZoneAroundPointer, call.Request.Kind);
        Assert.Equal(down, call.Request.Point);

        o.HandleInput(Up(1900, new ScreenPoint(50, 60)));
        await Idle(o);
        Assert.Single(_resolver.Calls);
    }

    [Fact]
    public async Task LongPressDisabled_UpWithoutDownIsIgnored()
    {
        var o = Create();

        o.HandleInput(Up(1000));
        await Idle(o);

        Assert.Empty(_resolver.Calls);
    }

    // ---------------------------------------------------------------------------------------------
    // Secondo clic = stop, annullamento
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SecondClickWhileSpeaking_StopsOnly()
    {
        _speech.BlockUntilStopped = true;
        _resolver.Handler = (_, _) => Task.FromResult(FakeResolver.Text("Una frase lunga da leggere."));
        var o = Create();

        o.HandleInput(Down(1000));
        Assert.True(await _speech.WaitStartedAsync());
        Assert.True(_speech.IsSpeaking);

        o.HandleInput(Down(3000));
        await Idle(o);

        Assert.Single(_resolver.Calls);
        Assert.Single(_speech.Requests);
        Assert.Equal(1, _speech.StopCount);
        Assert.False(_speech.IsSpeaking);

        // Il clic successivo, a voce ferma, legge di nuovo.
        _speech.BlockUntilStopped = false;
        o.HandleInput(Down(5000));
        await Idle(o);
        Assert.Equal(2, _resolver.Calls.Count);
    }

    [Fact]
    public async Task ClickWhileOtherSpeechIsPlaying_StopsOnly()
    {
        _speech.ExternalSpeaking = true; // es. rilettura della dettatura
        var o = Create();

        o.HandleInput(Down(1000));
        await Idle(o);

        Assert.Empty(_resolver.Calls);
        Assert.Equal(1, _speech.StopCount);
    }

    [Fact]
    public async Task NewRead_CancelsTheRunningResolution()
    {
        var first = new TaskCompletionSource();
        _resolver.Handler = async (r, ct) =>
        {
            if (r.Point == P)
            {
                first.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return FakeResolver.Text("Seconda.");
        };
        var o = Create();

        o.HandleInput(Down(1000));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        o.HandleInput(Down(3000, new ScreenPoint(1, 1)));
        await Idle(o);

        var calls = _resolver.Calls;
        Assert.Equal(2, calls.Count);
        Assert.True(calls[0].Token.IsCancellationRequested);
        Assert.False(calls[1].Token.IsCancellationRequested);
        Assert.Equal("Seconda.", Assert.Single(_speech.Requests).Text);
        Assert.Equal(0, _speech.StopCount);
    }

    [Fact]
    public async Task StopHotkey_StopsSpeechAndCancelsResolution()
    {
        var started = new TaskCompletionSource();
        _resolver.Handler = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeResolver.Text("Mai.");
        };
        var o = Create();

        o.HandleInput(Down(1000));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        o.HandleInput(Hotkey(HotkeyAction.Stop, 1100));
        await Idle(o);

        Assert.True(_resolver.Calls[0].Token.IsCancellationRequested);
        Assert.Equal(1, _speech.StopCount);
        Assert.Empty(_speech.Requests);
    }

    [Fact]
    public async Task HandleInput_NeverBlocks_EvenWithAStuckResolver()
    {
        _resolver.Handler = (_, _) => new TaskCompletionSource<ReadOutcome>().Task; // non finisce e ignora l'annullamento
        var o = Create();
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 1000; i++) o.HandleInput(Down(1000 + i * 1000L));

        Assert.True(sw.ElapsedMilliseconds < 1000, $"HandleInput troppo lento: {sw.ElapsedMilliseconds} ms");
        o.HandleInput(Hotkey(HotkeyAction.Stop, 5_000_000));
        await Flush(o);
        Assert.Equal(1, _speech.StopCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Scorciatoie e pausa
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadSelectionHotkey_RequestsSelection()
    {
        var o = Create();

        o.HandleInput(Hotkey(HotkeyAction.ReadSelection, 1000));
        await Idle(o);

        Assert.Equal(new ReadRequest(ReadRequestKind.Selection, P), Assert.Single(_resolver.Calls).Request);
    }

    [Fact]
    public async Task ReadHotkey_Repeated_IsDebounced()
    {
        var o = Create();

        o.HandleInput(Hotkey(HotkeyAction.ReadAtPointer, 1000));
        o.HandleInput(Hotkey(HotkeyAction.ReadAtPointer, 1030));
        await Idle(o);

        Assert.Single(_resolver.Calls);
    }

    [Fact]
    public async Task Paused_IgnoresMouse_ButHotkeysStillWork()
    {
        var o = Create();
        var changes = new List<bool>();
        o.PausedChanged += changes.Add;

        o.Paused = true;
        o.HandleInput(Down(1000));
        o.HandleInput(Up(1050));
        await Idle(o);
        Assert.Empty(_resolver.Calls);

        o.HandleInput(Hotkey(HotkeyAction.ReadAtPointer, 2000));
        await Idle(o);
        Assert.Single(_resolver.Calls);

        o.Paused = false;
        o.HandleInput(Down(4000));
        await Idle(o);
        Assert.Equal(2, _resolver.Calls.Count);
        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public async Task PausedFromSettings_AtStartup()
    {
        _settings.Current.General.Paused = true;
        var o = Create();

        Assert.True(o.Paused);
        o.HandleInput(Down(1000));
        await Idle(o);
        Assert.Empty(_resolver.Calls);
    }

    [Fact]
    public async Task LongPressTimer_DuringPause_IsIgnored()
    {
        _settings.Current.Input.LongPressEnabled = true;
        var o = Create();

        o.HandleInput(Down(1000));
        await Flush(o);
        o.Paused = true;
        _time.Advance(TimeSpan.FromSeconds(1));
        await Idle(o);

        Assert.Empty(_resolver.Calls);
    }

    [Fact]
    public async Task TogglePauseHotkey_TogglesAndConfirmsBySpeech()
    {
        var o = Create();

        o.HandleInput(Hotkey(HotkeyAction.TogglePause, 1000));
        await Idle(o);
        Assert.True(o.Paused);
        Assert.Equal(ReadOrchestrator.PausedText, _speech.Requests[^1].Text);
        Assert.Equal(SpeechKind.System, _speech.Requests[^1].Kind);

        o.HandleInput(Hotkey(HotkeyAction.TogglePause, 2000));
        await Idle(o);
        Assert.False(o.Paused);
        Assert.Equal(ReadOrchestrator.ResumedText, _speech.Requests[^1].Text);
    }

    [Fact]
    public async Task ToggleDictation_WithoutService_SaysNotAvailable()
    {
        var o = Create(dictation: null);

        o.HandleInput(Hotkey(HotkeyAction.ToggleDictation, 1000));
        await Idle(o);

        var spoken = Assert.Single(_speech.Requests);
        Assert.Equal("Dettatura non disponibile", spoken.Text);
        Assert.Equal(SpeechKind.System, spoken.Kind);
        Assert.Empty(_resolver.Calls);
    }

    [Fact]
    public async Task ToggleDictation_DelegatesToService()
    {
        var dictation = new FakeDictation();
        var o = Create(dictation);

        o.HandleInput(Hotkey(HotkeyAction.ToggleDictation, 1000));
        o.HandleInput(Hotkey(HotkeyAction.ToggleDictation, 1100));
        await Idle(o);

        // Le due chiamate non sono serializzate (la seconda pressione deve poter annullare la prima): si attende che arrivino entrambe.
        Assert.True(SpinWait.SpinUntil(() => dictation.Toggles == 2, 5000));
        Assert.Empty(_speech.Requests);
    }

    [Fact]
    public async Task ToggleDictation_StopsOurOwnReadingFirst()
    {
        _speech.BlockUntilStopped = true;
        var dictation = new FakeDictation();
        var o = Create(dictation);

        o.HandleInput(Down(1000));
        Assert.True(await _speech.WaitStartedAsync());
        o.HandleInput(Hotkey(HotkeyAction.ToggleDictation, 1500));
        await Idle(o);

        Assert.Equal(1, dictation.Toggles);
        Assert.Equal(1, _speech.StopCount);
        Assert.False(_speech.IsSpeaking);
    }

    [Fact]
    public async Task Dispose_IsSafe_AndLaterInputIsIgnored()
    {
        _speech.BlockUntilStopped = true;
        var o = Create();
        o.HandleInput(Down(1000));
        Assert.True(await _speech.WaitStartedAsync());

        o.Dispose();
        o.Dispose();
        o.HandleInput(Down(5000));

        Assert.Single(_resolver.Calls);
        Assert.DoesNotContain(_log.Entries, e => e.Level == "ERROR");
    }

    // ---------------------------------------------------------------------------------------------
    // Costruzione della richiesta vocale
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void BuildSpeechRequest_LongTextIsSplitIntoChunksOfLimitedLength()
    {
        var sentence = "Questa è una frase abbastanza lunga, con molte parole, per riempire il pezzo";
        var text = string.Join(", ", Enumerable.Repeat(sentence, 12)) + ".";
        var outcome = new ReadOutcome(ReadSource.Selection, text, SpeechKind.Sentence, "it", true, 1, "");

        var request = ReadOrchestrator.BuildSpeechRequest(outcome, new ReadingSettings(), NullLog.Instance);

        Assert.NotNull(request);
        Assert.NotNull(request!.Chunks);
        Assert.True(request.Chunks!.Count > 1);
        Assert.All(request.Chunks, c => Assert.True(c.Length <= ReadOrchestrator.MaxChunkChars));
        Assert.Equal(text, request.Text);
    }

    [Fact]
    public void BuildSpeechRequest_NothingAndEmptyNothingText_ReturnsNull()
    {
        var reading = new ReadingSettings { NothingFoundText = "  " };

        Assert.Null(ReadOrchestrator.BuildSpeechRequest(ReadOutcome.Nothing(1, ""), reading, NullLog.Instance));
    }
}
