using System.Collections.Concurrent;
using System.Diagnostics;

namespace PuntaEAscolta.Windows.Automation;

/// <summary>Cache pid -> nome del processo, con scadenza breve perché i pid vengono riutilizzati.</summary>
internal sealed class ProcessNameCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private const int MaxEntries = 256;

    private readonly ConcurrentDictionary<int, (string? Name, long Stamp)> _map = new();

    public string? Get(int pid)
    {
        if (pid <= 0) return null;
        long now = Stopwatch.GetTimestamp();
        if (_map.TryGetValue(pid, out var entry) && Stopwatch.GetElapsedTime(entry.Stamp, now) < Lifetime)
            return entry.Name;

        string? name = Lookup(pid);
        if (_map.Count >= MaxEntries) _map.Clear();
        _map[pid] = (name, now);
        return name;
    }

    private static string? Lookup(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // processo terminato o non interrogabile: il nome resta sconosciuto
            return null;
        }
    }
}
