using System.Text.Json.Serialization;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Core.Settings;

/// <summary>
/// Impostazioni dell'app, salvate in settings.json accanto all'eseguibile (cartella portatile),
/// con ripiego in %AppData%\PuntaEAscolta se la cartella non è scrivibile.
/// Tutti i valori hanno un predefinito sensato: un file mancante o parziale deve funzionare.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();
    public InputSettings Input { get; set; } = new();
    public ReadingSettings Reading { get; set; } = new();
    public OcrSettings Ocr { get; set; } = new();
    public SpeechSettings Speech { get; set; } = new();
    public DictationSettings Dictation { get; set; } = new();
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; } = false;
    public bool Paused { get; set; } = false;

    /// <summary>Se true il registro include il testo letto (solo per diagnosi, con il consenso dell'assistente).</summary>
    public bool DebugLog { get; set; } = false;
}

public sealed class InputSettings
{
    /// <summary>Pulsante del mouse che avvia la lettura. Predefinito: clic della rotellina.</summary>
    public TriggerButton MouseTrigger { get; set; } = TriggerButton.Middle;

    /// <summary>Accetta anche i clic generati da software (ausili di puntamento, emulatori). Predefinito: no.</summary>
    public bool AcceptInjectedEvents { get; set; } = false;

    /// <summary>Se attiva, la pressione prolungata legge tutta la zona attorno al puntatore invece del solo elemento.</summary>
    public bool LongPressEnabled { get; set; } = false;
    public int LongPressMs { get; set; } = 700;

    /// <summary>Anti-rimbalzo: i clic entro questo intervallo dal precedente vengono ignorati (clic involontari doppi).</summary>
    public int DebounceMs { get; set; } = 350;

    /// <summary>Scorciatoie da tastiera globali, in forma "Win+Shift+A". Stringa vuota = nessuna. Evitare Ctrl+Alt (è AltGr sulla tastiera italiana).</summary>
    public string HotkeyReadAtPointer { get; set; } = "";
    public string HotkeyReadSelection { get; set; } = "Win+Shift+F9";
    public string HotkeyStop { get; set; } = "";
    public string HotkeyTogglePause { get; set; } = "";

    /// <summary>Esc ferma la voce, ma solo mentre sta parlando.</summary>
    public bool EscStopsSpeech { get; set; } = true;
}

public sealed class ReadingSettings
{
    /// <summary>Se il puntatore è dentro un testo selezionato, il clic legge la selezione.</summary>
    public bool ReadSelectionWhenPointerInside { get; set; } = true;

    /// <summary>Nei documenti (Word, editor) legge la frase sotto il puntatore.</summary>
    public bool ReadSentenceInDocuments { get; set; } = true;

    /// <summary>Dice una breve frase quando non trova testo, così l'utente sa che il clic è stato ricevuto.</summary>
    public bool SpeakWhenNothingFound { get; set; } = true;
    public string NothingFoundText { get; set; } = "Nessun testo";

    /// <summary>Per caselle di controllo e voci con segno di spunta aggiunge "attivo" o "non attivo".</summary>
    public bool SpeakToggleState { get; set; } = false;

    /// <summary>Per le celle vuote di un foglio di calcolo.</summary>
    public string EmptyCellText { get; set; } = "Cella vuota";

    public bool StripKeyboardShortcuts { get; set; } = true;
    public bool StripEmoji { get; set; } = true;
    public int MaxCharsPerRead { get; set; } = 3000;

    /// <summary>Processi per i quali si salta l'accessibilità e si va subito all'OCR (nomi senza .exe, senza distinzione maiuscole).</summary>
    public List<string> OcrOnlyProcesses { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<OcrMode>))]
public enum OcrMode { Auto, WindowsOnly, OnnxOnly }

public sealed class OcrSettings
{
    public OcrMode Mode { get; set; } = OcrMode.Auto;

    /// <summary>Dimensioni della zona catturata attorno al puntatore, in pixel al 100% di scala (vengono moltiplicate per la scala del monitor).</summary>
    public int ZoneWidth { get; set; } = 900;
    public int ZoneHeight { get; set; } = 360;

    /// <summary>Distanza massima, in altezze di riga, fra il puntatore e la riga da leggere.</summary>
    public double MaxLineDistanceInLineHeights { get; set; } = 1.2;

    /// <summary>Raggruppa righe ravvicinate in un blocco (cartelli, paragrafi). Le voci di menu, più distanziate, restano righe singole.</summary>
    public bool GroupLinesIntoBlocks { get; set; } = true;

    /// <summary>Spazio verticale massimo fra due righe dello stesso blocco, in frazione dell'altezza di riga.</summary>
    public double BlockMaxGapInLineHeights { get; set; } = 0.65;

    public string WindowsOcrLanguage { get; set; } = "it-IT";
}

[JsonConverter(typeof(JsonStringEnumConverter<SpeechProviderKind>))]
public enum SpeechProviderKind
{
    /// <summary>ElevenLabs se la chiave è presente e la rete risponde, altrimenti la voce di Windows.</summary>
    Auto,
    Windows,
    ElevenLabs
}

[JsonConverter(typeof(JsonStringEnumConverter<LabelLanguageMode>))]
public enum LabelLanguageMode { Auto, Italian, English }

public sealed class SpeechSettings
{
    public SpeechProviderKind Provider { get; set; } = SpeechProviderKind.Auto;
    public double Volume { get; set; } = 1.0;

    // Voce di Windows
    public string WindowsVoiceName { get; set; } = "";
    /// <summary>Velocità relativa: 1.0 normale, 0.5 lenta, 2.0 veloce.</summary>
    public double WindowsRate { get; set; } = 1.0;

    // ElevenLabs
    /// <summary>Chiave API protetta con ISecretProtector (mai in chiaro nel file).</summary>
    public string ElevenLabsProtectedApiKey { get; set; } = "";
    public string ElevenLabsVoiceId { get; set; } = "";
    public string ElevenLabsVoiceName { get; set; } = "";
    /// <summary>Modello per frasi e blocchi di testo (qualità).</summary>
    public string ElevenLabsSentenceModel { get; set; } = "eleven_multilingual_v2";
    /// <summary>Modello per etichette brevi. Se vuoto usa il modello delle frasi.</summary>
    public string ElevenLabsLabelModel { get; set; } = "eleven_flash_v2_5";
    public string ElevenLabsOutputFormat { get; set; } = "pcm_44100";
    public double ElevenLabsStability { get; set; } = 0.5;
    public double ElevenLabsSimilarity { get; set; } = 0.75;
    public double ElevenLabsStyle { get; set; } = 0.0;
    public double ElevenLabsSpeed { get; set; } = 1.0;
    public int FirstAudioTimeoutLabelMs { get; set; } = 1500;
    public int FirstAudioTimeoutSentenceMs { get; set; } = 4000;
    public LabelLanguageMode LabelLanguage { get; set; } = LabelLanguageMode.Auto;

    /// <summary>Riservatezza: il testo dei documenti viene letto solo con la voce di Windows e non lascia il PC.</summary>
    public bool DocumentsWithLocalVoiceOnly { get; set; } = false;

    public bool CacheEnabled { get; set; } = true;
    public int CacheMaxMegabytes { get; set; } = 500;
}

[JsonConverter(typeof(JsonStringEnumConverter<DictationInsertMode>))]
public enum DictationInsertMode { Unicode, Paste }

public sealed class DictationSettings
{
    public bool Enabled { get; set; } = false;
    public string Hotkey { get; set; } = "Win+Shift+D";
    public TriggerButton MouseTrigger { get; set; } = TriggerButton.None;
    public string MicrophoneDeviceId { get; set; } = "";
    public string Language { get; set; } = "it";
    public string Model { get; set; } = "scribe_v2";
    /// <summary>Rilegge a voce il testo riconosciuto prima di inserirlo.</summary>
    public bool ReadBack { get; set; } = true;
    /// <summary>Dopo la rilettura inserisce da solo il testo (una nuova pressione durante la rilettura annulla).</summary>
    public bool AutoInsert { get; set; } = true;
    public DictationInsertMode InsertMode { get; set; } = DictationInsertMode.Unicode;
    public int MaxSeconds { get; set; } = 90;
    /// <summary>Comandi vocali a frase intera, modificabili dall'assistente.</summary>
    public List<string> CommandDelete { get; set; } = new() { "cancella", "cancella tutto" };
    public List<string> CommandNewLine { get; set; } = new() { "a capo", "vai a capo" };
    public List<string> CommandReadAgain { get; set; } = new() { "rileggi" };
}

/// <summary>Archivio delle impostazioni. Save è atomico (file temporaneo + rinomina).</summary>
public interface ISettingsStore
{
    string FilePath { get; }
    /// <summary>Cartella dati (cache audio, log), accanto alle impostazioni.</summary>
    string DataDirectory { get; }
    AppSettings Current { get; }
    event Action<AppSettings>? Changed;
    AppSettings Load();
    void Save(AppSettings settings);
}
