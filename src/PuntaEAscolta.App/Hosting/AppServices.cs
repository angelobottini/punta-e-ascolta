using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Logging;
using PuntaEAscolta.Logic.Reading;
using PuntaEAscolta.Logic.Settings;
using PuntaEAscolta.Ocr.Onnx;
using PuntaEAscolta.Speech;
using PuntaEAscolta.Speech.Cache;
using PuntaEAscolta.Speech.Dictation;
using PuntaEAscolta.Speech.ElevenLabs;
using PuntaEAscolta.Windows.Audio;
using PuntaEAscolta.Windows.Automation;
using PuntaEAscolta.Windows.Input;
using PuntaEAscolta.Windows.Ocr;

namespace PuntaEAscolta.App.Hosting;

internal sealed class AppServicesOptions
{
    /// <summary>Modifiche temporanee alle impostazioni (solo in memoria), es. --provider della riga di comando.</summary>
    public Action<AppSettings>? Overrides { get; init; }

    /// <summary>Avvolge lettore e voce locale in sonde di misura (riga di comando).</summary>
    public bool MeasureSpeech { get; init; }
}

/// <summary>
/// Radice di composizione: crea tutti i servizi nell'ordine delle dipendenze e li chiude in ordine inverso.
/// Orchestratore e sorgente di input sono creati solo dalla modalità icona di notifica (<see cref="Tray.TrayController"/>).
/// </summary>
internal sealed class AppServices : IDisposable
{
    private readonly List<(string Name, IDisposable Item)> _owned = new();
    private int _disposed;

    private AppServices(AppLog log)
    {
        Log = log;
    }

    public static string AppDirectory => AppContext.BaseDirectory;

    public static string Version =>
        typeof(AppServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppServices).Assembly.GetName().Version?.ToString()
        ?? "0";

    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public AppLog Log { get; }
    public JsonSettingsStore SettingsStore { get; private set; } = null!;

    /// <summary>Archivio passato ai servizi: quello vero, oppure una copia in memoria con le modifiche temporanee.</summary>
    public ISettingsStore Settings { get; private set; } = null!;

    public FileLog FileLog { get; private set; } = null!;
    public DpapiSecretProtector Protector { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public WindowsOcrEngine WindowsOcr { get; private set; } = null!;
    public OnnxOcrEngine OnnxOcr { get; private set; } = null!;
    public GdiScreenCapture Capture { get; private set; } = null!;
    public UiaTextSource Uia { get; private set; } = null!;
    public ClipboardSelectionReader Clipboard { get; private set; } = null!;
    public NAudioPlayer Player { get; private set; } = null!;
    public WindowsVoiceSynthesizer WindowsVoice { get; private set; } = null!;
    public ElevenLabsSynthesizer ElevenLabs { get; private set; } = null!;
    public SpeechCache Cache { get; private set; } = null!;
    public SpeechService Speech { get; private set; } = null!;
    public NAudioRecorder Recorder { get; private set; } = null!;
    public ElevenLabsSpeechToText SpeechToText { get; private set; } = null!;
    public SendInputTextInjector Injector { get; private set; } = null!;
    public DictationService Dictation { get; private set; } = null!;
    public TextResolver Resolver { get; private set; } = null!;

    /// <summary>Sonde di misura (solo con <see cref="AppServicesOptions.MeasureSpeech"/>).</summary>
    public MeasuredAudioPlayer? MeasuredPlayer { get; private set; }
    public MeasuredSynthesizer? MeasuredLocalVoice { get; private set; }

    public static AppServices Create(AppLog log, AppServicesOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        options ??= new AppServicesOptions();
        var services = new AppServices(log);
        try
        {
            services.Compose(options);
            return services;
        }
        catch (Exception ex)
        {
            log.Error("Composizione dei servizi non riuscita", ex);
            services.Dispose();
            throw;
        }
    }

    private void Compose(AppServicesOptions options)
    {
        // Impostazioni e registro: il registro su file va nella cartella dati scelta dall'archivio.
        var store = OpenSettings(Log);
        SettingsStore = store;
        FileLog = Log.File!;

        if (options.Overrides is not null)
        {
            var snapshot = JsonSettingsStore.Clone(store.Current);
            options.Overrides(snapshot);
            Settings = new ScopedSettingsStore(store, snapshot);
        }
        else
        {
            Settings = store;
        }
        var settings = Settings;

        Protector = new DpapiSecretProtector();
        Http = CreateHttpClient();
        Own("HttpClient", Http);

        // OCR: Windows primario, ONNX secondario (inizializzazione pigra, riscaldamento a cura di chi avvia l'app).
        WindowsOcr = new WindowsOcrEngine(() => settings.Current.Ocr.WindowsOcrLanguage, Log);
        Own("OCR Windows", WindowsOcr);
        OnnxOcr = new OnnxOcrEngine(Log);
        Own("OCR ONNX", OnnxOcr);

        Capture = new GdiScreenCapture();
        Uia = new UiaTextSource(Log);
        Own("UI Automation", Uia);
        Clipboard = new ClipboardSelectionReader(Log);
        OwnIfDisposable("Appunti", Clipboard);

        // Voce.
        Player = new NAudioPlayer(Log);
        Own("Lettore audio", Player);
        WindowsVoice = new WindowsVoiceSynthesizer(settings, Log);
        Own("Voce di Windows", WindowsVoice);
        ElevenLabs = new ElevenLabsSynthesizer(Http, settings, Protector, Log);
        Own("ElevenLabs", ElevenLabs);
        Cache = new SpeechCache(Path.Combine(settings.DataDirectory, "cache"), () => settings.Current.Speech.CacheMaxMegabytes, Log);

        IAudioPlayer player = Player;
        ISpeechSynthesizer localVoice = WindowsVoice;
        if (options.MeasureSpeech)
        {
            MeasuredPlayer = new MeasuredAudioPlayer(Player);
            MeasuredLocalVoice = new MeasuredSynthesizer(WindowsVoice);
            player = MeasuredPlayer;
            localVoice = MeasuredLocalVoice;
        }
        Speech = new SpeechService(ElevenLabs, localVoice, player, Cache, settings, Log);
        Own("Servizio vocale", Speech);

        // Dettatura.
        Recorder = new NAudioRecorder(Log);
        Own("Microfono", Recorder);
        SpeechToText = new ElevenLabsSpeechToText(Http, settings, Protector, Log);
        OwnIfDisposable("Trascrizione", SpeechToText);
        Injector = new SendInputTextInjector(Log);
        OwnIfDisposable("Inserimento testo", Injector);
        Dictation = new DictationService(Recorder, SpeechToText, Injector, Speech, settings, Log);
        Own("Dettatura", Dictation);

        Resolver = new TextResolver(Uia, Capture, WindowsOcr, OnnxOcr, Clipboard, settings, Log);
    }

    /// <summary>
    /// Solo archivio impostazioni e registro su file (cartella dati\logs), per i comandi che non servono altro.
    /// Il registro resta collegato a <paramref name="log"/>, che lo chiude.
    /// </summary>
    public static JsonSettingsStore OpenSettings(AppLog log)
    {
        var store = new JsonSettingsStore(AppDirectory, log);
        var file = new FileLog(Path.Combine(store.DataDirectory, "logs"), () => store.Current.General.DebugLog);
        log.Attach(file);
        log.Info($"Punta e Ascolta {Version} ({Architecture}), cartella {AppDirectory}, impostazioni {store.FilePath}" +
                 (store.IsPortable ? "" : " (ripiego in AppData)"));
        return store;
    }

    /// <summary>Un solo HttpClient per ElevenLabs (sintesi, trascrizione, account). Il Timeout vale fino alle intestazioni.</summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PuntaEAscolta/{Version}");
        return http;
    }

    private void Own(string name, IDisposable item) => _owned.Add((name, item));

    private void OwnIfDisposable(string name, object item)
    {
        if (item is IDisposable d) _owned.Add((name, d));
    }

    /// <summary>Chiude i servizi in ordine inverso di creazione (dettatura prima della voce, voce prima del lettore...).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            var (name, item) = _owned[i];
            try
            {
                item.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error($"Chiusura di {name} non riuscita", ex);
            }
        }
        _owned.Clear();
        Log.Info("Servizi chiusi");
        Log.Flush();
    }
}
