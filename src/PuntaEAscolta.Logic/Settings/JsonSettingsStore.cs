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
/// Un file presente ma non leggibile (bloccato all'accesso da antivirus o sincronizzazione) viene riprovato qualche volta;
/// se resta illeggibile si usano i predefiniti in memoria e il primo salvataggio copia prima il file in <c>settings.json.bak</c>,
/// così le impostazioni vere non vanno mai perse.
/// Salvataggio atomico: file temporaneo e poi sostituzione. Per cambiare un solo valore senza riscrivere quelli cambiati da
/// fuori (modifica a mano, riga di comando) si usa <see cref="Update"/>, che rilegge il file sotto il lock.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public const string FileName = "settings.json";
    public const string FallbackFolderName = "PuntaEAscolta";

    /// <summary>Estensione della copia di sicurezza fatta prima di sovrascrivere un file che non si era riusciti a leggere.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>Attese fra i tentativi di lettura di un file bloccato (in tutto meno di un secondo, solo in quel caso).</summary>
    internal static readonly TimeSpan[] ReadRetryDelays =
    {
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500),
    };

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

    /// <summary>
    /// Vero se il file esiste ma non è stato letto (o interpretato, in <see cref="Update"/>): il prossimo salvataggio lo copia
    /// prima in <c>settings.json.bak</c>. Si azzera con una lettura riuscita o dopo la copia. Protetto da <c>_gate</c>.
    /// </summary>
    private bool _backupBeforeSave;

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
        _current = LoadCore() ?? new AppSettings();
    }

    public string FilePath { get; }

    public string DataDirectory { get; }

    /// <summary>True se le impostazioni stanno nella cartella dell'app (modalità portatile), false se nel ripiego %AppData%.</summary>
    public bool IsPortable { get; }

    /// <summary>Vero se il file c'è ma non è stato letto: il prossimo salvataggio ne farà prima una copia in <c>settings.json.bak</c>.</summary>
    public bool BackupPending
    {
        get { lock (_gate) return _backupBeforeSave; }
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<AppSettings>? Changed;

    /// <summary>
    /// Rilegge il file (o i predefiniti) e aggiorna <see cref="Current"/>, notificando <see cref="Changed"/>. Se il file c'è ma
    /// non si legge, <see cref="Current"/> resta com'era.
    /// </summary>
    public AppSettings Load()
    {
        AppSettings loaded;
        lock (_gate)
        {
            loaded = LoadCore() ?? _current;
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
            copy = SaveCore(settings);
        }
        _log.Info("Impostazioni salvate");
        RaiseChanged(copy);
    }

    /// <summary>
    /// Modifica mirata: sotto il lock rilegge <c>settings.json</c> dal disco (se esiste e si interpreta), applica
    /// <paramref name="mutate"/> e salva. Così i valori cambiati da fuori mentre l'app è aperta (modifica a mano, un'altra
    /// copia dell'app) non vengono riscritti con quelli vecchi in memoria. Se il file manca si parte da <see cref="Current"/>;
    /// se c'è ma non si legge o non si interpreta si parte da <see cref="Current"/> e il file viene prima copiato in
    /// <c>settings.json.bak</c>. Restituisce la copia salvata; <see cref="Changed"/> la notifica (con gli eventuali valori
    /// cambiati da fuori, che così vengono anche applicati).
    /// </summary>
    public AppSettings Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        AppSettings copy;
        lock (_gate)
        {
            var (status, fromDisk, error) = ReadFile(retry: true);
            AppSettings target;
            switch (status)
            {
                case ReadStatus.Ok:
                    target = fromDisk!;
                    _backupBeforeSave = false;
                    break;
                case ReadStatus.Missing:
                    target = Clone(_current);
                    break;
                default:
                    _log.Warn($"File impostazioni non leggibile durante l'aggiornamento ({error?.GetType().Name}: {error?.Message}): parto dai valori in memoria e ne faccio prima una copia");
                    _backupBeforeSave = true;
                    target = Clone(_current);
                    break;
            }
            mutate(target);
            copy = SaveCore(target);
        }
        _log.Info("Impostazioni aggiornate");
        RaiseChanged(copy);
        return copy;
    }

    /// <summary>Scrittura atomica sotto il lock; se serve, copia di sicurezza del file che non si era riusciti a leggere.</summary>
    private AppSettings SaveCore(AppSettings settings)
    {
        Sanitize(settings);
        var directory = Path.GetDirectoryName(FilePath)!;
        var temp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            if (_backupBeforeSave && File.Exists(FilePath))
            {
                // Se la copia non riesce il salvataggio fallisce: meglio non salvare che perdere il file vero.
                var backup = FilePath + BackupSuffix;
                File.Copy(FilePath, backup, overwrite: true);
                _log.Warn($"Il file impostazioni non era stato letto: copiato in {Path.GetFileName(backup)} prima di sovrascriverlo");
            }
            _backupBeforeSave = false;
            var json = JsonSerializer.Serialize(settings, Options);
            File.WriteAllText(temp, json, Utf8NoBom);
            File.Move(temp, FilePath, overwrite: true);
            var copy = Clone(settings);
            _current = copy;
            return copy;
        }
        catch (Exception ex)
        {
            _log.Error("Salvataggio delle impostazioni fallito", ex);
            TryDelete(temp);
            throw;
        }
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

    /// <summary>
    /// Lettura all'avvio e in <see cref="Load"/> (sotto il lock o nel costruttore). File mancante: predefiniti. File corrotto:
    /// rinominato <c>.bad</c>, predefiniti. File presente ma illeggibile anche dopo i nuovi tentativi: null (chi chiama decide
    /// che cosa tenere) e copia di sicurezza al prossimo salvataggio.
    /// </summary>
    private AppSettings? LoadCore()
    {
        var (status, settings, error) = ReadFile(retry: true);
        switch (status)
        {
            case ReadStatus.Ok:
                _backupBeforeSave = false;
                return settings;
            case ReadStatus.Missing:
                _backupBeforeSave = false;
                _log.Info($"Nessun file impostazioni in {FilePath}: uso i predefiniti");
                return new AppSettings();
            case ReadStatus.Corrupt:
                _backupBeforeSave = false;
                Quarantine(error!);
                return new AppSettings();
            default:
                _backupBeforeSave = true;
                _log.Error($"Lettura del file impostazioni fallita dopo {ReadRetryDelays.Length + 1} tentativi: uso i valori predefiniti in memoria; " +
                           $"il file resta com'è e al primo salvataggio verrà copiato in {FileName}{BackupSuffix}", error);
                return null;
        }
    }

    private enum ReadStatus { Missing, Ok, Unreadable, Corrupt }

    /// <summary>
    /// Legge e interpreta il file senza rinominare né modificare nulla. Un file bloccato (IOException, accesso negato) viene
    /// riprovato con le attese di <see cref="ReadRetryDelays"/> se <paramref name="retry"/> è vero.
    /// </summary>
    private (ReadStatus Status, AppSettings? Settings, Exception? Error) ReadFile(bool retry)
    {
        if (!File.Exists(FilePath)) return (ReadStatus.Missing, null, null);

        string json;
        int attempt = 0;
        while (true)
        {
            try
            {
                json = File.ReadAllText(FilePath);
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return (ReadStatus.Missing, null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!retry || attempt >= ReadRetryDelays.Length) return (ReadStatus.Unreadable, null, ex);
                var delay = ReadRetryDelays[attempt++];
                _log.Warn($"File impostazioni non leggibile ({ex.GetType().Name}): nuovo tentativo fra {delay.TotalMilliseconds:0} ms");
                Thread.Sleep(delay);
            }
            catch (Exception ex)
            {
                return (ReadStatus.Unreadable, null, ex);
            }
        }

        try
        {
            if (string.IsNullOrWhiteSpace(json)) throw new JsonException("file vuoto");
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? throw new JsonException("documento JSON nullo");
            Sanitize(settings);
            return (ReadStatus.Ok, settings, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException)
        {
            return (ReadStatus.Corrupt, null, ex);
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
