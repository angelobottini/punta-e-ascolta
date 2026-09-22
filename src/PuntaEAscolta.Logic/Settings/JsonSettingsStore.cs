using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Logic.Settings;

/// <summary>
/// Archivio delle impostazioni su file JSON (<c>settings.json</c>).
/// Posizione: la cartella dell'app se scrivibile (cartella portatile), altrimenti <c>%AppData%\PuntaEAscolta</c>.
/// <see cref="DataDirectory"/> è la stessa cartella: lì vanno <c>cache</c> e <c>logs</c>.
/// Tollerante a file mancante, parziale o con membri sconosciuti; un file corrotto viene rinominato <c>.bad</c> e si riparte dai predefiniti.
/// Salvataggio atomico: file temporaneo e poi sostituzione.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public const string FileName = "settings.json";
    public const string FallbackFolderName = "PuntaEAscolta";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // Lettere accentate leggibili nel file (l'assistente lo apre con un editor di testo).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            // TriggerButton non ha l'attributo nel Core: enum come stringhe per tutti.
            new JsonStringEnumConverter<TriggerButton>(),
            new JsonStringEnumConverter<OcrMode>(),
            new JsonStringEnumConverter<SpeechProviderKind>(),
            new JsonStringEnumConverter<LabelLanguageMode>(),
            new JsonStringEnumConverter<DictationInsertMode>(),
        },
    };

    private readonly ILog _log;
    private readonly object _gate = new();
    private AppSettings _current;

    public JsonSettingsStore(string appDirectory, ILog? log = null)
        : this(appDirectory, DefaultFallbackDirectory(), log)
    {
    }

    /// <summary>Costruttore con cartella di ripiego esplicita (per i test: evita di scrivere nel vero %AppData%).</summary>
    internal JsonSettingsStore(string appDirectory, string fallbackDirectory, ILog? log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);
        _log = log ?? NullLog.Instance;

        string baseDirectory;
        if (IsWritable(appDirectory))
        {
            baseDirectory = Path.GetFullPath(appDirectory);
            IsPortable = true;
        }
        else
        {
            baseDirectory = Path.GetFullPath(fallbackDirectory);
            IsPortable = false;
            _log.Info($"La cartella dell'app non è scrivibile: impostazioni e dati in {baseDirectory}");
            try { Directory.CreateDirectory(baseDirectory); }
            catch (Exception ex) { _log.Error("Impossibile creare la cartella di ripiego delle impostazioni", ex); }
        }

        DataDirectory = baseDirectory;
        FilePath = Path.Combine(baseDirectory, FileName);
        _current = LoadCore();
    }

    public string FilePath { get; }

    public string DataDirectory { get; }

    /// <summary>True se le impostazioni stanno nella cartella dell'app (modalità portatile), false se nel ripiego %AppData%.</summary>
    public bool IsPortable { get; }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<AppSettings>? Changed;

    /// <summary>Rilegge il file (o i predefiniti) e aggiorna <see cref="Current"/>, notificando <see cref="Changed"/>.</summary>
    public AppSettings Load()
    {
        AppSettings loaded;
        lock (_gate)
        {
            loaded = LoadCore();
            _current = loaded;
        }
        RaiseChanged(loaded);
        return loaded;
    }

    /// <summary>Salva in modo atomico e aggiorna <see cref="Current"/> con una copia indipendente dell'oggetto passato.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings copy;
        lock (_gate)
        {
            Sanitize(settings);
            var directory = Path.GetDirectoryName(FilePath)!;
            var temp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(settings, Options);
                File.WriteAllText(temp, json, Utf8NoBom);
                File.Move(temp, FilePath, overwrite: true);
                copy = Clone(settings);
                _current = copy;
            }
            catch (Exception ex)
            {
                _log.Error("Salvataggio delle impostazioni fallito", ex);
                TryDelete(temp);
                throw;
            }
        }
        _log.Info("Impostazioni salvate");
        RaiseChanged(copy);
    }

    /// <summary>Copia indipendente (utile alla finestra impostazioni per lavorare su una bozza).</summary>
    public static AppSettings Clone(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, Options);
        var copy = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        Sanitize(copy);
        return copy;
    }

    private AppSettings LoadCore()
    {
        if (!File.Exists(FilePath))
        {
            _log.Info($"Nessun file impostazioni in {FilePath}: uso i predefiniti");
            return new AppSettings();
        }

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex)
        {
            _log.Error("Lettura del file impostazioni fallita: uso i predefiniti", ex);
            return new AppSettings();
        }

        try
        {
            if (string.IsNullOrWhiteSpace(json)) throw new JsonException("file vuoto");
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? throw new JsonException("documento JSON nullo");
            Sanitize(settings);
            return settings;
        }
        catch (JsonException ex)
        {
            Quarantine(ex);
            return new AppSettings();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FormatException or OverflowException)
        {
            Quarantine(ex);
            return new AppSettings();
        }
    }

    /// <summary>File corrotto: lo rinomina in <c>settings.json.bad</c> (sovrascrivendo un eventuale .bad precedente).</summary>
    private void Quarantine(Exception reason)
    {
        var bad = FilePath + ".bad";
        try
        {
            File.Move(FilePath, bad, overwrite: true);
            _log.Warn($"File impostazioni corrotto ({reason.Message}): rinominato in {Path.GetFileName(bad)}, uso i predefiniti");
        }
        catch (Exception ex)
        {
            _log.Error($"File impostazioni corrotto ({reason.Message}) e impossibile rinominarlo: uso i predefiniti", ex);
        }
    }

    private void RaiseChanged(AppSettings settings)
    {
        var handlers = Changed;
        if (handlers is null) return;
        try { handlers(settings); }
        catch (Exception ex) { _log.Error("Un gestore dell'evento Changed delle impostazioni ha lanciato un'eccezione", ex); }
    }

    /// <summary>Riporta a valori utilizzabili ciò che un file scritto a mano potrebbe aver reso nullo o privo di senso.</summary>
    internal static void Sanitize(AppSettings s)
    {
        s.General ??= new GeneralSettings();
        s.Input ??= new InputSettings();
        s.Reading ??= new ReadingSettings();
        s.Ocr ??= new OcrSettings();
        s.Speech ??= new SpeechSettings();
        s.Dictation ??= new DictationSettings();
        if (s.SchemaVersion <= 0) s.SchemaVersion = 1;

        var i = s.Input;
        i.HotkeyReadAtPointer ??= "";
        i.HotkeyReadSelection ??= "";
        i.HotkeyStop ??= "";
        i.HotkeyTogglePause ??= "";
        if (i.LongPressMs < 100) i.LongPressMs = 100;
        if (i.DebounceMs < 0) i.DebounceMs = 0;

        var r = s.Reading;
        r.NothingFoundText ??= "";
        r.EmptyCellText ??= "";
        r.OcrOnlyProcesses ??= new List<string>();
        r.OcrOnlyProcesses.RemoveAll(string.IsNullOrWhiteSpace);
        if (r.MaxCharsPerRead <= 0) r.MaxCharsPerRead = 3000;

        var o = s.Ocr;
        o.WindowsOcrLanguage ??= "";
        if (o.ZoneWidth < 50) o.ZoneWidth = 900;
        if (o.ZoneHeight < 30) o.ZoneHeight = 360;
        if (o.MaxLineDistanceInLineHeights <= 0) o.MaxLineDistanceInLineHeights = 1.2;
        if (o.BlockMaxGapInLineHeights <= 0) o.BlockMaxGapInLineHeights = 0.65;

        var v = s.Speech;
        v.WindowsVoiceName ??= "";
        v.ElevenLabsProtectedApiKey ??= "";
        v.ElevenLabsVoiceId ??= "";
        v.ElevenLabsVoiceName ??= "";
        v.ElevenLabsSentenceModel ??= "";
        v.ElevenLabsLabelModel ??= "";
        v.ElevenLabsOutputFormat ??= "";
        if (v.Volume < 0) v.Volume = 0;
        if (v.Volume > 1) v.Volume = 1;
        if (v.WindowsRate <= 0) v.WindowsRate = 1.0;
        if (v.FirstAudioTimeoutLabelMs <= 0) v.FirstAudioTimeoutLabelMs = 1500;
        if (v.FirstAudioTimeoutSentenceMs <= 0) v.FirstAudioTimeoutSentenceMs = 4000;
        if (v.CacheMaxMegabytes < 0) v.CacheMaxMegabytes = 0;

        var d = s.Dictation;
        d.Hotkey ??= "";
        d.MicrophoneDeviceId ??= "";
        d.Language ??= "";
        d.Model ??= "";
        d.CommandDelete ??= new List<string>();
        d.CommandNewLine ??= new List<string>();
        d.CommandReadAgain ??= new List<string>();
        if (d.MaxSeconds <= 0) d.MaxSeconds = 90;
    }

    /// <summary>Prova concreta di scrittura: crea e cancella un file temporaneo nella cartella.</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".prova-scrittura-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DefaultFallbackDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(appData)) appData = Path.GetTempPath();
        return Path.Combine(appData, FallbackFolderName);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* ignorato */ }
    }
}
