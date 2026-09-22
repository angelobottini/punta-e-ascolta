using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Speech.Dictation;
using PuntaEAscolta.Speech.Tests.Fakes;
using Xunit;

namespace PuntaEAscolta.Speech.Tests;

public sealed class DictationServiceTests
{
    private sealed class Rig : IDisposable
    {
        private readonly List<DictationState> _states = new();

        public Rig(Action<AppSettings>? tweak = null, TimeProvider? time = null)
        {
            var settings = new AppSettings();
            settings.Dictation.Enabled = true;
            tweak?.Invoke(settings);
            Settings = new FakeSettingsStore(settings);
            Service = new DictationService(Recorder, Stt, Injector, Speech, Settings, Log, time ?? TimeProvider.System)
            {
                ReadBackGrace = TimeSpan.Zero
            };
            Service.StateChanged += s =>
            {
                lock (_states) _states.Add(s);
            };
        }

        public FakeRecorder Recorder { get; } = new();
        public FakeSpeechToText Stt { get; } = new();
        public FakeInjector Injector { get; } = new();
        public FakeSpeechService Speech { get; } = new();
        public CollectingLog Log { get; } = new();
        public FakeSettingsStore Settings { get; }
        public DictationService Service { get; }

        public DictationState[] States
        {
            get { lock (_states) return _states.ToArray(); }
        }

        public Task ToggleAsync() => Service.ToggleAsync(CancellationToken.None).Bounded();

        /// <summary>Un ciclo completo: avvio, arresto, attesa della fine di trascrizione, rilettura e inserimento.</summary>
        public async Task DictateAsync(string transcript)
        {
            Stt.Script(transcript);
            await ToggleAsync();
            Assert.Equal(DictationState.Recording, Service.State);
            await ToggleAsync();
            await Service.PipelineTask.Bounded();
        }

        public void Dispose() => Service.Dispose();
    }

    [Fact]
    public async Task Disabled_or_unconfigured_dictation_says_not_available()
    {
        using var disabled = new Rig(s => s.Dictation.Enabled = false);
        await disabled.ToggleAsync();
        Assert.Equal(["Dettatura non disponibile"], disabled.Speech.Texts);
        Assert.Equal(SpeechKind.System, disabled.Speech.Spoken[0].Kind);
        Assert.Empty(disabled.Recorder.StartedDevices);
        Assert.Equal(DictationState.Idle, disabled.Service.State);

        using var noKey = new Rig();
        noKey.Stt.IsConfigured = false;
        await noKey.ToggleAsync();
        Assert.Equal(["Dettatura non disponibile"], noKey.Speech.Texts);
        Assert.Empty(noKey.Recorder.StartedDevices);
        Assert.Empty(noKey.States);
    }

    [Fact]
    public async Task First_toggle_says_Registro_then_records_on_the_configured_microphone()
    {
        using var rig = new Rig(s => s.Dictation.MicrophoneDeviceId = "mic1");

        await rig.ToggleAsync();

        Assert.Equal(["Registro"], rig.Speech.Texts);
        Assert.Equal(["mic1"], rig.Recorder.StartedDevices);
        Assert.Equal(DictationState.Recording, rig.Service.State);
        Assert.Equal([DictationState.Recording], rig.States);
    }

    [Fact]
    public async Task Missing_configured_microphone_falls_back_to_default_and_no_microphone_is_announced()
    {
        using var rig = new Rig(s => s.Dictation.MicrophoneDeviceId = "usb");
        rig.Recorder.FailingDevice = "usb";
        await rig.ToggleAsync();
        Assert.Equal(["usb", null], rig.Recorder.StartedDevices);
        Assert.Equal(DictationState.Recording, rig.Service.State);

        using var none = new Rig();
        none.Recorder.FailAlways = true;
        await none.ToggleAsync();
        Assert.Equal(["Registro", "Microfono non disponibile"], none.Speech.Texts);
        Assert.Equal(DictationState.Idle, none.Service.State);
    }

    [Fact]
    public async Task Full_cycle_transcribes_reads_back_and_inserts_with_trailing_space()
    {
        using var rig = new Rig();
        rig.Log.IsDebugEnabled = false;

        await rig.DictateAsync("ciao mondo");

        var call = Assert.Single(rig.Stt.Calls);
        Assert.Equal(rig.Recorder.Recording, call.Pcm);
        Assert.Equal("it", call.Language);
        Assert.Equal(["Registro", "Ricevuto", "ciao mondo"], rig.Speech.Texts);
        var readBack = rig.Speech.Spoken[2];
        Assert.Equal(SpeechKind.Sentence, readBack.Kind);
        Assert.True(readBack.Sensitive);
        Assert.Equal("it", readBack.LanguageHint);
        Assert.Equal(["type:ciao mondo "], rig.Injector.Actions);
        Assert.Equal("ciao mondo ", rig.Service.LastInsertedText);
        Assert.Equal([DictationState.Recording, DictationState.Transcribing, DictationState.ReadingBack, DictationState.Idle], rig.States);
        Assert.Equal(1, rig.Recorder.StopCount);
        Assert.False(rig.Log.Contains("ciao mondo")); // testo solo a livello Debug
    }

    [Fact]
    public async Task Empty_transcript_says_non_ho_capito()
    {
        using var rig = new Rig();
        await rig.DictateAsync("   ");

        Assert.Equal(["Registro", "Ricevuto", "Non ho capito"], rig.Speech.Texts);
        Assert.Empty(rig.Injector.Actions);
        Assert.Equal([DictationState.Recording, DictationState.Transcribing, DictationState.Idle], rig.States);
    }

    [Fact]
    public async Task Voice_commands_delete_new_line_and_read_again()
    {
        using var rig = new Rig();

        await rig.DictateAsync("Ciao a tutti");
        await rig.DictateAsync("Rileggi.");
        await rig.DictateAsync("Cancella.");
        await rig.DictateAsync("cancella");
        await rig.DictateAsync("vai a capo");
        await rig.DictateAsync("cancella");
        await rig.DictateAsync("rileggi");

        Assert.Equal(["type:Ciao a tutti ", "backspace:13", "enter", "backspace:1"], rig.Injector.Actions);
        var spoken = rig.Speech.Texts.Where(t => t is not ("Registro" or "Ricevuto")).ToArray();
        Assert.Equal(["Ciao a tutti", "Ciao a tutti", "Cancellato", "Niente da cancellare", "A capo", "Cancellato", "Niente da rileggere"], spoken);
        var reread = rig.Speech.Spoken.Where(r => r.Text == "Ciao a tutti").ToArray();
        Assert.All(reread, r => Assert.Equal(SpeechKind.Sentence, r.Kind));
        Assert.Null(rig.Service.LastInsertedText);
    }

    [Fact]
    public async Task Toggle_during_read_back_cancels_without_inserting()
    {
        using var rig = new Rig();
        rig.Speech.BlockSentences = true;
        rig.Stt.Script("testo sbagliato");
        await rig.ToggleAsync();
        await rig.ToggleAsync();
        await TestUtil.WaitUntilAsync(() => rig.Service.State == DictationState.ReadingBack && rig.Speech.IsBlocking, what: "rilettura");

        await rig.ToggleAsync();
        await rig.Service.PipelineTask.Bounded();

        Assert.Empty(rig.Injector.Actions);
        Assert.Equal(1, rig.Speech.Texts.Count(t => t == "Annullato"));
        Assert.Equal("Annullato", rig.Speech.Texts[^1]);
        Assert.Equal(DictationState.Idle, rig.Service.State);
        Assert.True(rig.Speech.StopCount >= 1);
    }

    [Fact]
    public async Task Voice_stopped_by_the_user_during_read_back_cancels_insertion()
    {
        using var rig = new Rig();
        rig.Speech.BlockSentences = true;
        rig.Stt.Script("testo");
        await rig.ToggleAsync();
        await rig.ToggleAsync();
        await TestUtil.WaitUntilAsync(() => rig.Speech.IsBlocking, what: "rilettura");

        rig.Speech.Stop(); // clic della rotellina: l'orchestratore ferma la voce
        await rig.Service.PipelineTask.Bounded();

        Assert.Empty(rig.Injector.Actions);
        Assert.Equal("Annullato", rig.Speech.Texts[^1]);
    }

    [Fact]
    public async Task Read_back_replaced_by_another_reading_cancels_silently()
    {
        using var rig = new Rig();
        rig.Speech.ForcedSentenceOutcome = SpeechOutcome.Superseded;

        await rig.DictateAsync("testo");

        Assert.Empty(rig.Injector.Actions);
        Assert.Equal(["Registro", "Ricevuto", "testo"], rig.Speech.Texts);
        Assert.Equal(DictationState.Idle, rig.Service.State);
    }

    [Fact]
    public async Task Toggle_during_transcription_cancels()
    {
        using var rig = new Rig();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Stt.Behaviour = async (_, _, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return "mai";
        };
        await rig.ToggleAsync();
        await rig.ToggleAsync();
        Assert.Equal(DictationState.Transcribing, rig.Service.State);
        await TestUtil.WaitUntilAsync(() => rig.Stt.Calls.Count == 1, what: "trascrizione avviata");

        await rig.ToggleAsync();
        await rig.Service.PipelineTask.Bounded();
        await cancelled.Task.Bounded();

        Assert.Empty(rig.Injector.Actions);
        Assert.Equal("Annullato", rig.Speech.Texts[^1]);
        Assert.Equal(DictationState.Idle, rig.Service.State);
    }

    [Fact]
    public async Task Max_duration_stops_recording_automatically()
    {
        var time = new ManualTimeProvider();
        using var rig = new Rig(s => s.Dictation.MaxSeconds = 5, time);
        rig.Stt.Script("dettato lungo");

        await rig.ToggleAsync();
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(DictationState.Recording, rig.Service.State);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(DictationState.Recording, rig.Service.State);
        await rig.Service.PipelineTask.Bounded();

        Assert.Equal(1, rig.Recorder.StopCount);
        Assert.Equal(["type:dettato lungo "], rig.Injector.Actions);
        Assert.True(rig.Log.Contains("durata massima"));
    }

    [Fact]
    public async Task Transcription_gives_up_after_30_seconds()
    {
        var time = new ManualTimeProvider();
        using var rig = new Rig(time: time);
        rig.Stt.Behaviour = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "mai";
        };
        await rig.ToggleAsync();
        await rig.ToggleAsync();
        await TestUtil.WaitUntilAsync(() => rig.Stt.Calls.Count == 1, what: "trascrizione avviata");

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(DictationState.Transcribing, rig.Service.State);
        time.Advance(TimeSpan.FromSeconds(1));
        await rig.Service.PipelineTask.Bounded();

        Assert.Equal("Dettatura non riuscita", rig.Speech.Texts[^1]);
        Assert.Empty(rig.Injector.Actions);
        Assert.Equal(DictationState.Idle, rig.Service.State);
    }

    [Fact]
    public async Task Transcription_errors_are_announced()
    {
        using var rig = new Rig();
        rig.Stt.Behaviour = (_, _, _) => throw new SpeechToTextException(SpeechToTextFailure.Network, "rete assente");
        await rig.ToggleAsync();
        await rig.ToggleAsync();
        await rig.Service.PipelineTask.Bounded();
        Assert.Equal("Dettatura non riuscita", rig.Speech.Texts[^1]);

        rig.Stt.Behaviour = (_, _, _) => Task.FromException<string>(new SpeechToTextException(SpeechToTextFailure.NotConfigured, "chiave assente"));
        await rig.ToggleAsync();
        await rig.ToggleAsync();
        await rig.Service.PipelineTask.Bounded();
        Assert.Equal("Dettatura non disponibile", rig.Speech.Texts[^1]);
        Assert.Empty(rig.Injector.Actions);
    }

    [Fact]
    public async Task Without_auto_insert_a_new_toggle_confirms_and_silence_cancels()
    {
        var time = new ManualTimeProvider();
        using var rig = new Rig(s => s.Dictation.AutoInsert = false, time);

        rig.Stt.Script("da confermare");
        await rig.ToggleAsync();
        await rig.ToggleAsync();
        await TestUtil.WaitUntilAsync(() => rig.Log.Contains("in attesa di conferma"), what: "attesa della conferma");
        Assert.Equal(DictationState.ReadingBack, rig.Service.State);
        await rig.ToggleAsync();
        await rig.Service.PipelineTask.Bounded();
        Assert.Equal(["type:da confermare "], rig.Injector.Actions);

        rig.Stt.Script("mai confermato");
        await rig.ToggleAsync();
        await rig.ToggleAsync();
        await TestUtil.WaitUntilAsync(() => rig.Log.Entries.Count(e => e.Contains("in attesa di conferma")) == 2, what: "seconda attesa");
        time.Advance(TimeSpan.FromSeconds(15));
        await rig.Service.PipelineTask.Bounded();
        Assert.Single(rig.Injector.Actions);
        Assert.Equal("Annullato", rig.Speech.Texts[^1]);
    }

    [Fact]
    public async Task Without_read_back_text_is_inserted_right_away()
    {
        using var rig = new Rig(s => s.Dictation.ReadBack = false);
        await rig.DictateAsync("subito");

        Assert.Equal(["Registro", "Ricevuto"], rig.Speech.Texts);
        Assert.Equal(["type:subito "], rig.Injector.Actions);
        Assert.Equal([DictationState.Recording, DictationState.Transcribing, DictationState.Idle], rig.States);
    }

    [Fact]
    public async Task Toggle_during_the_pause_after_read_back_cancels()
    {
        using var rig = new Rig();
        rig.Service.ReadBackGrace = TimeSpan.FromSeconds(5);
        rig.Stt.Script("quasi inserito");
        await rig.ToggleAsync();
        await rig.ToggleAsync();
        await TestUtil.WaitUntilAsync(() => rig.Speech.Texts.Contains("quasi inserito"), what: "rilettura finita");

        await rig.ToggleAsync();
        await rig.Service.PipelineTask.Bounded();

        Assert.Empty(rig.Injector.Actions);
        Assert.Equal("Annullato", rig.Speech.Texts[^1]);
    }

    [Fact]
    public async Task Cancel_during_recording_discards_the_audio()
    {
        using var rig = new Rig();
        await rig.ToggleAsync();

        rig.Service.Cancel();

        Assert.Equal(DictationState.Idle, rig.Service.State);
        Assert.Equal(1, rig.Recorder.StopCount);
        Assert.Empty(rig.Stt.Calls);
        await TestUtil.WaitUntilAsync(() => rig.Speech.Texts.Contains("Annullato"), what: "annuncio");
        rig.Service.Cancel(); // idempotente
        Assert.Equal([DictationState.Recording, DictationState.Idle], rig.States);
    }

    [Fact]
    public async Task Language_setting_reaches_transcription_and_read_back()
    {
        using var rig = new Rig(s => s.Dictation.Language = "en");
        await rig.DictateAsync("hello");

        Assert.Equal("en", rig.Stt.Calls[0].Language);
        Assert.Equal("en", rig.Speech.Spoken.Single(r => r.Text == "hello").LanguageHint);
    }

    [Fact]
    public async Task Injector_failure_is_announced_and_nothing_is_remembered()
    {
        using var rig = new Rig();
        var failing = new ThrowingInjector();
        var service = new DictationService(rig.Recorder, rig.Stt, failing, rig.Speech, rig.Settings, rig.Log) { ReadBackGrace = TimeSpan.Zero };
        rig.Stt.Script("testo");

        await service.ToggleAsync(CancellationToken.None).Bounded();
        await service.ToggleAsync(CancellationToken.None).Bounded();
        await service.PipelineTask.Bounded();

        Assert.Equal("Inserimento non riuscito", rig.Speech.Texts[^1]);
        Assert.Null(service.LastInsertedText);
        Assert.Equal(DictationState.Idle, service.State);
        service.Dispose();
    }

    [Fact]
    public async Task Dispose_during_recording_stops_the_microphone_silently()
    {
        var rig = new Rig();
        await rig.ToggleAsync();

        rig.Service.Dispose();
        await rig.Service.ToggleAsync(CancellationToken.None).Bounded();

        Assert.Equal(1, rig.Recorder.StopCount);
        Assert.False(rig.Recorder.IsRecording);
        Assert.Equal(["Registro"], rig.Speech.Texts);
    }

    [Fact]
    public async Task Cancel_racing_with_stopped_read_back_never_inserts_the_rejected_text()
    {
        // Revisione del 22/09/2026: il thread dell'annullamento ha già deciso (_cancelling) ma non ha ancora annullato il
        // token quando la rilettura, fermata, risveglia la pipeline. Prima il testo annullato veniva inserito lo stesso.
        using var rig = new Rig();
        rig.Speech.BlockSentences = true;
        rig.Stt.Script("testo rifiutato");
        await rig.ToggleAsync();
        await rig.ToggleAsync();
        await TestUtil.WaitUntilAsync(() => rig.Service.State == DictationState.ReadingBack && rig.Speech.IsBlocking, what: "rilettura");

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object session = typeof(DictationService).GetField("_session", flags)!.GetValue(rig.Service)!;
        var sessionType = session.GetType();
        sessionType.GetField("_cancelling", flags)!.SetValue(session, true);
        var cts = (CancellationTokenSource)sessionType.GetField("_cts", flags)!.GetValue(session)!;

        rig.Speech.Stop();                 // passo 1 dell'annullamento: la voce si ferma, la pipeline riparte
        await Task.Delay(100);
        cts.Cancel();                      // passo 2, in ritardo: il token si annulla
        sessionType.GetField("_cancelling", flags)!.SetValue(session, false);
        await rig.Service.PipelineTask.Bounded();

        Assert.Empty(rig.Injector.Actions);
        Assert.Equal(DictationState.Idle, rig.Service.State);
    }

    private sealed class ThrowingInjector : ITextInjector
    {
        public Task TypeTextAsync(string text, CancellationToken ct) => throw new InvalidOperationException("finestra protetta");
        public Task PressEnterAsync(CancellationToken ct) => throw new InvalidOperationException("finestra protetta");
        public Task PressBackspaceAsync(int count, CancellationToken ct) => throw new InvalidOperationException("finestra protetta");
    }
}
