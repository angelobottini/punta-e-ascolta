using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Settings;

namespace PuntaEAscolta.App.Hosting;

/// <summary>
/// Archivio di impostazioni solo in memoria, con gli stessi percorsi dell'archivio vero. Serve per le prove che non
/// devono toccare settings.json: "Prova voce" della finestra impostazioni (bozza non salvata) e le opzioni della riga
/// di comando (es. --provider).
/// </summary>
internal sealed class ScopedSettingsStore : ISettingsStore
{
    private readonly object _gate = new();
    private AppSettings _current;

    public ScopedSettingsStore(ISettingsStore origin, AppSettings snapshot)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(snapshot);
        FilePath = origin.FilePath;
        DataDirectory = origin.DataDirectory;
        _current = JsonSettingsStore.Clone(snapshot);
    }

    public string FilePath { get; }

    public string DataDirectory { get; }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<AppSettings>? Changed;

    public AppSettings Load() => Current;

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = JsonSettingsStore.Clone(settings);
        lock (_gate) _current = copy;
        Changed?.Invoke(copy);
    }
}
