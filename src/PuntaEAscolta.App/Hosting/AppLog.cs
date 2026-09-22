using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Logic.Logging;

namespace PuntaEAscolta.App.Hosting;

/// <summary>
/// Registro dell'app: inoltra al <see cref="FileLog"/> appena la cartella dati è nota (i messaggi precedenti, ad esempio
/// quelli dell'archivio impostazioni, restano in memoria e vengono scritti dopo). Facoltativamente copia i messaggi su
/// stderr (riga di comando con --verbose). La chiave API non compare mai: FileLog la maschera e la copia su stderr passa
/// dalla stessa maschera.
/// </summary>
internal sealed class AppLog : ILog, IDisposable
{
    private const int MaxPending = 500;

    private readonly object _gate = new();
    private readonly List<(char Level, string Message, Exception? Exception)> _pending = new();
    private FileLog? _file;
    private bool _disposed;

    /// <summary>Copia ogni messaggio su stderr (solo riga di comando).</summary>
    public bool MirrorToStandardError { get; init; }

    /// <summary>Forza il livello Debug (riga di comando con --verbose): i messaggi Debug vanno solo su stderr se il file non li accetta.</summary>
    public bool ForceDebug { get; init; }

    public FileLog? File
    {
        get { lock (_gate) return _file; }
    }

    public bool IsDebugEnabled
    {
        get
        {
            if (ForceDebug) return true;
            var file = File;
            return file is not null && file.IsDebugEnabled;
        }
    }

    /// <summary>Collega il file di registro e scarica i messaggi arrivati prima.</summary>
    public void Attach(FileLog file)
    {
        ArgumentNullException.ThrowIfNull(file);
        List<(char, string, Exception?)> pending;
        lock (_gate)
        {
            _file = file;
            pending = new List<(char, string, Exception?)>(_pending);
            _pending.Clear();
        }
        foreach (var (level, message, exception) in pending) WriteToFile(file, level, message, exception);
    }

    public void Debug(string message)
    {
        if (!IsDebugEnabled) return;
        Write('D', message, null);
    }

    public void Info(string message) => Write('I', message, null);

    public void Warn(string message) => Write('W', message, null);

    public void Error(string message, Exception? exception = null) => Write('E', message, exception);

    public void Flush()
    {
        try { File?.Flush(); } catch { /* il registro non deve mai far fallire l'app */ }
    }

    private void Write(char level, string message, Exception? exception)
    {
        message ??= "";
        FileLog? file;
        lock (_gate)
        {
            if (_disposed) return;
            file = _file;
            if (file is null)
            {
                if (_pending.Count < MaxPending) _pending.Add((level, message, exception));
            }
        }
        if (file is not null) WriteToFile(file, level, message, exception);
        if (MirrorToStandardError) Mirror(level, message, exception);
    }

    private static void WriteToFile(FileLog file, char level, string message, Exception? exception)
    {
        try
        {
            switch (level)
            {
                case 'D': file.Debug(message); break;
                case 'I': file.Info(message); break;
                case 'W': file.Warn(message); break;
                default: file.Error(message, exception); break;
            }
        }
        catch
        {
            // FileLog non lancia; per prudenza nessuna eccezione verso il chiamante.
        }
    }

    private static void Mirror(char level, string message, Exception? exception)
    {
        try
        {
            string label = level switch { 'D' => "DEBUG", 'I' => "INFO ", 'W' => "WARN ", _ => "ERROR" };
            string text = $"[{DateTime.Now:HH:mm:ss.fff}] {label} {message}";
            if (exception is not null) text += $" ({exception.GetType().Name}: {exception.Message})";
            Console.Error.WriteLine(FileLog.Mask(text));
        }
        catch
        {
            // stderr chiuso: si ignora.
        }
    }

    public void Dispose()
    {
        FileLog? file;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            file = _file;
            _file = null;
        }
        try { file?.Dispose(); } catch { /* ignorato */ }
    }
}
