using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Settings;
using PuntaEAscolta.Logic.Tests.Fakes;
using Xunit;

namespace PuntaEAscolta.Logic.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PuntaEAscoltaTests", "settings-" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();

    public JsonSettingsStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignorato */ }
    }

    private string AppDir => Path.Combine(_root, "app");
    private string FallbackDir => Path.Combine(_root, "fallback");
    private string SettingsFile => Path.Combine(AppDir, JsonSettingsStore.FileName);

    private JsonSettingsStore Create() => new(AppDir, FallbackDir, _log);

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(AppDir);
        File.WriteAllText(SettingsFile, json);
    }

    [Fact]
    public void MissingFile_UsesDefaults_InTheAppDirectory()
    {
        var store = Create();

        Assert.True(store.IsPortable);
        Assert.Equal(Path.GetFullPath(SettingsFile), store.FilePath);
        Assert.Equal(Path.GetFullPath(AppDir), store.DataDirectory);
        Assert.Equal(TriggerButton.Middle, store.Current.Input.MouseTrigger);
        Assert.Equal("Nessun testo", store.Current.Reading.NothingFoundText);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void SaveAndReload_RoundTrip_EnumsWrittenAsStrings()
    {
        var store = Create();
        var s = JsonSettingsStore.Clone(store.Current);
        s.Input.MouseTrigger = TriggerButton.X2;
        s.Input.LongPressEnabled = true;
        s.Input.DebounceMs = 420;
        s.Ocr.Mode = OcrMode.OnnxOnly;
        s.Speech.Provider = SpeechProviderKind.ElevenLabs;
        s.Speech.LabelLanguage = LabelLanguageMode.English;
        s.Speech.Volume = 0.15;
        s.Reading.NothingFoundText = "Qui non c'è niente";
        s.Reading.OcrOnlyProcesses.Add("Photo");
        s.Dictation.InsertMode = DictationInsertMode.Paste;

        store.Save(s);
        var json = File.ReadAllText(SettingsFile);
        var reloaded = new JsonSettingsStore(AppDir, FallbackDir, _log).Current;

        Assert.Contains("\"X2\"", json);
        Assert.Contains("\"OnnxOnly\"", json);
        Assert.Contains("\"ElevenLabs\"", json);
        Assert.Contains("Qui non c'è niente", json); // lettere accentate leggibili
        Assert.Equal(TriggerButton.X2, reloaded.Input.MouseTrigger);
        Assert.True(reloaded.Input.LongPressEnabled);
        Assert.Equal(420, reloaded.Input.DebounceMs);
        Assert.Equal(OcrMode.OnnxOnly, reloaded.Ocr.Mode);
        Assert.Equal(SpeechProviderKind.ElevenLabs, reloaded.Speech.Provider);
        Assert.Equal(LabelLanguageMode.English, reloaded.Speech.LabelLanguage);
        Assert.Equal(0.15, reloaded.Speech.Volume, 6);
        Assert.Equal("Qui non c'è niente", reloaded.Reading.NothingFoundText);
        Assert.Equal(new[] { "Photo" }, reloaded.Reading.OcrOnlyProcesses);
        Assert.Equal(DictationInsertMode.Paste, reloaded.Dictation.InsertMode);
    }

    [Fact]
    public void CorruptFile_IsRenamedBad_AndDefaultsAreUsed()
    {
        WriteFile("{ \"Input\": { \"DebounceMs\": 500, ");

        var store = Create();

        Assert.Equal(350, store.Current.Input.DebounceMs);
        Assert.False(File.Exists(SettingsFile));
        Assert.True(File.Exists(SettingsFile + ".bad"));
        Assert.Contains(_log.Entries, e => e.Level == "WARN" && e.Message.Contains(".bad", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyFile_IsTreatedAsCorrupt()
    {
        WriteFile("   ");

        var store = Create();

        Assert.NotNull(store.Current.Input);
        Assert.True(File.Exists(SettingsFile + ".bad"));
    }

    [Fact]
    public void WrongEnumValue_IsTreatedAsCorrupt()
    {
        WriteFile("{ \"Ocr\": { \"Mode\": \"Magia\" } }");

        var store = Create();

        Assert.Equal(OcrMode.Auto, store.Current.Ocr.Mode);
        Assert.True(File.Exists(SettingsFile + ".bad"));
    }

    [Fact]
    public void PartialFile_MissingMembersKeepDefaults_UnknownMembersIgnored()
    {
        WriteFile("""
            {
              // commento scritto dall'assistente
              "input": { "debounceMs": "500", "MouseTrigger": "X1" },
              "Ocr": { "Mode": "WindowsOnly" },
              "MembroFuturo": 42,
            }
            """);

        var store = Create();

        Assert.Equal(500, store.Current.Input.DebounceMs);
        Assert.Equal(TriggerButton.X1, store.Current.Input.MouseTrigger);
        Assert.Equal(700, store.Current.Input.LongPressMs);
        Assert.Equal(OcrMode.WindowsOnly, store.Current.Ocr.Mode);
        Assert.Equal(900, store.Current.Ocr.ZoneWidth);
        Assert.True(store.Current.Reading.SpeakWhenNothingFound);
        Assert.False(File.Exists(SettingsFile + ".bad"));
    }

    [Fact]
    public void NullsAndNonsense_AreSanitized()
    {
        WriteFile("""
            { "Reading": null, "Input": { "HotkeyStop": null, "LongPressMs": 1 },
              "Speech": { "Volume": 7 }, "Ocr": { "ZoneWidth": 0 }, "Dictation": { "CommandDelete": null } }
            """);

        var s = Create().Current;

        Assert.NotNull(s.Reading);
        Assert.NotNull(s.Reading.OcrOnlyProcesses);
        Assert.Equal("", s.Input.HotkeyStop);
        Assert.Equal(100, s.Input.LongPressMs);
        Assert.Equal(1.0, s.Speech.Volume);
        Assert.Equal(900, s.Ocr.ZoneWidth);
        Assert.NotNull(s.Dictation.CommandDelete);
    }

    [Fact]
    public void Save_RaisesChanged_StoresIndependentCopy_AndLeavesNoTempFile()
    {
        var store = Create();
        AppSettings? notified = null;
        store.Changed += s => notified = s;
        var draft = JsonSettingsStore.Clone(store.Current);
        draft.Input.DebounceMs = 999;

        store.Save(draft);
        draft.Input.DebounceMs = 1;

        Assert.NotNull(notified);
        Assert.Equal(999, store.Current.Input.DebounceMs);
        Assert.NotSame(draft, store.Current);
        Assert.False(File.Exists(SettingsFile + ".tmp"));
    }

    [Fact]
    public void Load_RereadsTheFileAndNotifies()
    {
        var store = Create();
        int changes = 0;
        store.Changed += _ => changes++;
        WriteFile("{ \"Input\": { \"DebounceMs\": 250 } }");

        var loaded = store.Load();

        Assert.Equal(250, loaded.Input.DebounceMs);
        Assert.Equal(250, store.Current.Input.DebounceMs);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ChangedHandlerThrowing_DoesNotBreakSave()
    {
        var store = Create();
        store.Changed += _ => throw new InvalidOperationException("gestore guasto");

        store.Save(JsonSettingsStore.Clone(store.Current));

        Assert.True(File.Exists(SettingsFile));
        Assert.Contains(_log.Entries, e => e.Level == "ERROR");
    }

    [Fact]
    public void NotWritableAppDirectory_UsesFallbackDirectory()
    {
        // Un file al posto della cartella: la cartella dell'app non è creabile né scrivibile.
        var blocked = Path.Combine(_root, "bloccata");
        File.WriteAllText(blocked, "non sono una cartella");

        var store = new JsonSettingsStore(blocked, FallbackDir, _log);
        store.Save(JsonSettingsStore.Clone(store.Current));

        Assert.False(store.IsPortable);
        Assert.Equal(Path.GetFullPath(FallbackDir), store.DataDirectory);
        Assert.True(File.Exists(Path.Combine(FallbackDir, JsonSettingsStore.FileName)));
    }

    [Fact]
    public void PublicConstructor_WithWritableDirectory_IsPortable()
    {
        var store = new JsonSettingsStore(AppDir);

        Assert.True(store.IsPortable);
        Assert.StartsWith(Path.GetFullPath(AppDir), store.FilePath, StringComparison.OrdinalIgnoreCase);
    }
}
