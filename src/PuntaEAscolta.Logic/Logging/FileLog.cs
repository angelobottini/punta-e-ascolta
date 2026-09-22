using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Logic.Logging;

/// <summary>
/// Registro su file giornaliero: <c>punta-e-ascolta-AAAAMMGG.log</c> nella cartella indicata, al massimo 7 file.
/// Scrittura protetta da lock, mai eccezioni verso il chiamante, Flush a ogni Warn ed Error.
/// I messaggi Debug vengono scritti solo se <c>debugEnabled()</c> restituisce true.
/// Rete di sicurezza: qualunque cosa somigli a una chiave API (intestazione <c>xi-api-key</c>, token <c>sk_...</c>) viene mascherata.
/// </summary>
public sealed partial class FileLog : ILog, IDisposable
{
    public const string FilePrefix = "punta-e-ascolta-";
    public const string FileExtension = ".log";
    public const int MaxFiles = 7;

    private static readonly TimeSpan InfoFlushInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly Func<bool> _debugEnabled;
    private readonly object _gate = new();

    private StreamWriter? _writer;
    private DateOnly _openDay;
    private DateTime _lastFlush;
    private DateTime _retryAfter = DateTime.MinValue;
    private bool _disposed;

    /// <summary>Orologio locale (sostituibile nei test per provare la rotazione giornaliera).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public FileLog(string directory, Func<bool> debugEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(debugEnabled);
        _directory = directory;
        _debugEnabled = debugEnabled;
    }

    /// <summary>Cartella dei file di registro.</summary>
    public string DirectoryPath => _directory;

    /// <summary>Percorso del file del giorno corrente (null se non è ancora stato aperto nulla).</summary>
    public string? CurrentFilePath
    {
        get { lock (_gate) return _writer is null ? null : Path.Combine(_directory, FileNameFor(_openDay)); }
    }

    public bool IsDebugEnabled
    {
        get
        {
            try { return _debugEnabled(); }
            catch { return false; }
        }
    }

    public void Debug(string message)
    {
        if (!IsDebugEnabled) return;
        Write("DEBUG", message, null);
    }

    public void Info(string message) => Write("INFO ", message, null);

    public void Warn(string message) => Write("WARN ", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>Scrive su disco quanto ancora in memoria. Non lancia mai eccezioni.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            try { _writer?.Flush(); }
            catch { /* mai verso il chiamante */ }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseWriter();
        }
    }

    public static string FileNameFor(DateOnly day) =>
        FilePrefix + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + FileExtension;

    /// <summary>Maschera ciò che somiglia a una chiave API. Pubblico perché utile anche a chi compone messaggi di log.</summary>
    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var masked = XiApiKeyRegex().Replace(text, "${1}***");
        masked = SkTokenRegex().Replace(masked, "sk_***");
        return masked;
    }

    private void Write(string level, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_disposed) return;
            DateTime now;
            try { now = Clock(); }
            catch { now = DateTime.Now; }

            try
            {
                EnsureWriter(now);
                if (_writer is null) return;

                _writer.Write(FormatLine(now, level, message, exception));

                bool important = level[0] is 'W' or 'E';
                if (important || now - _lastFlush >= InfoFlushInterval || now < _lastFlush)
                {
                    _writer.Flush();
                    _lastFlush = now;
                }
            }
            catch
            {
                // Disco pieno, file bloccato, cartella sparita: si chiude e si riprova più tardi. Mai eccezioni verso il chiamante.
                CloseWriter();
                _retryAfter = now + RetryAfterFailure;
            }
        }
    }

    private void EnsureWriter(DateTime now)
    {
        var day = DateOnly.FromDateTime(now);
        if (_writer is not null && day == _openDay) return;

        CloseWriter();
        if (now < _retryAfter) return;

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, FileNameFor(day));
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096);
        _writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false, NewLine = "\r\n" };
        _openDay = day;
        _lastFlush = now;
        Prune(path);
    }

    private void CloseWriter()
    {
        if (_writer is null) return;
        try { _writer.Flush(); } catch { /* ignorato */ }
        try { _writer.Dispose(); } catch { /* ignorato */ }
        _writer = null;
    }

    /// <summary>Elimina i file di registro più vecchi lasciandone al massimo <see cref="MaxFiles"/> (il nome con la data ordina cronologicamente).</summary>
    private void Prune(string currentPath)
    {
        try
        {
            var files = Directory.GetFiles(_directory, FilePrefix + "*" + FileExtension)
                .Where(f => IsLogFileName(Path.GetFileName(f)))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var keep = new HashSet<string>(files.Take(MaxFiles), StringComparer.OrdinalIgnoreCase) { currentPath };
            foreach (var file in files.Skip(MaxFiles))
            {
                if (keep.Contains(file)) continue;
                try { File.Delete(file); } catch { /* un file in uso resta: si riproverà domani */ }
            }
        }
        catch { /* la rotazione non deve mai disturbare la scrittura */ }
    }

    private static bool IsLogFileName(string name) =>
        name.Length == FilePrefix.Length + 8 + FileExtension.Length
        && name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)
        && name.AsSpan(FilePrefix.Length, 8).IndexOfAnyExceptInRange('0', '9') < 0;

    private static string FormatLine(DateTime now, string level, string message, Exception? exception)
    {
        var sb = new StringBuilder(128);
        sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append(' ').Append(level)
          .Append(" [").Append(Environment.CurrentManagedThreadId.ToString("D2", CultureInfo.InvariantCulture)).Append("] ")
          .Append(message ?? "");
        if (exception is not null)
        {
            sb.Append("\r\n    ").Append(Describe(exception).Replace("\n", "\n    ", StringComparison.Ordinal));
        }
        sb.Append("\r\n");
        return Mask(sb.ToString());
    }

    /// <summary>
    /// Descrizione di un'eccezione. Per HttpRequestException NON si usa ToString(): si riportano solo tipo, messaggio, codice di stato
    /// e traccia dello stack, così nessuna intestazione HTTP (chiave API) finisce nel registro.
    /// </summary>
    private static string Describe(Exception exception)
    {
        if (ContainsHttpException(exception))
        {
            var sb = new StringBuilder();
            Exception? current = exception;
            int depth = 0;
            while (current is not null && depth < 5)
            {
                if (depth > 0) sb.Append("\n---> ");
                sb.Append(current.GetType().FullName).Append(": ").Append(current.Message);
                if (current is HttpRequestException hre && hre.StatusCode is { } code)
                    sb.Append(" (HTTP ").Append((int)code).Append(')');
                current = current.InnerException;
                depth++;
            }
            if (!string.IsNullOrEmpty(exception.StackTrace)) sb.Append('\n').Append(exception.StackTrace);
            return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        string text;
        try { text = exception.ToString(); }
        catch { text = exception.GetType().FullName + ": " + exception.Message; }
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static bool ContainsHttpException(Exception exception)
    {
        Exception? current = exception;
        int depth = 0;
        while (current is not null && depth < 8)
        {
            if (current is HttpRequestException) return true;
            current = current.InnerException;
            depth++;
        }
        return false;
    }

    [GeneratedRegex("""(xi-api-key["']?\s*[:=]\s*["']?)([A-Za-z0-9_\-]{32,})""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XiApiKeyRegex();

    [GeneratedRegex("""\bsk_[A-Za-z0-9]{16,}""", RegexOptions.CultureInvariant)]
    private static partial Regex SkTokenRegex();
}
