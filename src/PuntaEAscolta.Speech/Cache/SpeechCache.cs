using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Speech.Cache;

/// <summary>
/// Componenti della chiave di cache. Il testo viene normalizzato (spazi compressi, Trim, NFC) ma NON portato in minuscolo:
/// il maiuscolo può cambiare la pronuncia delle sigle.
/// </summary>
public sealed record SpeechCacheKey(
    string Provider,
    string VoiceId,
    string Model,
    string? Language,
    string OutputFormat,
    string VoiceSettings,
    string Text)
{
    public const string Version = "v1";

    public string NormalizedText => NormalizeText(Text);

    /// <summary>SHA-256 esadecimale minuscolo (64 caratteri) di tutte le componenti.</summary>
    public string ComputeHash()
    {
        string canonical = string.Join('|', Version, Provider, VoiceId, Model, Language ?? "-", OutputFormat, VoiceSettings, NormalizedText);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        string collapsed = sb.ToString();
        try
        {
            return collapsed.IsNormalized(NormalizationForm.FormC) ? collapsed : collapsed.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return collapsed; // sequenze Unicode non valide: si usa il testo così com'è
        }
    }
}

/// <summary>
/// Cache su disco dell'audio PCM già sintetizzato. File &lt;cartella&gt;\&lt;ab&gt;\&lt;hash&gt;.pcm con intestazione propria di 16 byte
/// (magic 'PEA1', frequenza int32, canali int16, bit int16, 4 byte riservati) seguita dal PCM grezzo little endian.
/// Scrittura atomica (file .tmp poi rinomina) e solo di audio completo. Limite di dimensione con eliminazione dei meno
/// usati di recente in base a LastWriteTimeUtc, aggiornato a ogni hit (LastAccessTime può essere disattivato su NTFS).
/// Nessun indice: il totale viene calcolato alla prima operazione con una enumerazione e poi tenuto in memoria.
/// Tutte le operazioni sono sicure fra thread e non lanciano eccezioni verso il chiamante (si registra e si degrada).
/// </summary>
public sealed class SpeechCache
{
    public const string FileExtension = ".pcm";
    public const string TempExtension = ".tmp";
    public const int HeaderSize = 16;
    private static readonly byte[] Magic = "PEA1"u8.ToArray();
    private const double EvictionTargetRatio = 0.9;

    private readonly Func<int> _maxMegabytes;
    private readonly ILog _log;
    private readonly object _sync = new();
    private long _sizeBytes;
    private bool _scanned;

    public SpeechCache(string directory, Func<int> maxMegabytes, ILog log)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Cartella della cache non indicata.", nameof(directory));
        Directory = System.IO.Path.GetFullPath(directory);
        _maxMegabytes = maxMegabytes ?? throw new ArgumentNullException(nameof(maxMegabytes));
        _log = log ?? NullLog.Instance;
    }

    public string Directory { get; }

    /// <summary>Limite in byte; 0 se la cache è disattivata (limite non positivo).</summary>
    public long MaxBytes
    {
        get
        {
            int mb;
            try { mb = _maxMegabytes(); } catch (Exception) { mb = 0; }
            return mb <= 0 ? 0 : mb * 1024L * 1024L;
        }
    }

    public bool TryOpen(SpeechCacheKey key, [NotNullWhen(true)] out Stream? pcm, [NotNullWhen(true)] out PcmFormat? format)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TryOpen(key.ComputeHash(), out pcm, out format);
    }

    /// <summary>
    /// Apre l'audio in cache; lo Stream restituito è posizionato sul primo campione PCM e va chiuso dal chiamante.
    /// Aggiorna LastWriteTimeUtc (uso recente). Un file corrotto viene eliminato e trattato come assente.
    /// </summary>
    public bool TryOpen(string hash, [NotNullWhen(true)] out Stream? pcm, [NotNullWhen(true)] out PcmFormat? format)
    {
        pcm = null;
        format = null;
        if (!IsValidHash(hash)) return false;
        string path = PathFor(hash);
        FileStream? stream = null;
        try
        {
            if (!File.Exists(path)) return false;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[HeaderSize];
            if (stream.Length < HeaderSize || stream.Read(header) != HeaderSize || !TryParseHeader(header, out var parsed))
            {
                stream.Dispose();
                stream = null;
                DeleteCorrupt(path);
                return false;
            }
            Touch(path);
            pcm = stream;
            format = parsed;
            return true;
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            _log.Warn($"Cache audio: lettura fallita ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
    }

    public bool Contains(SpeechCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string hash = key.ComputeHash();
        return IsValidHash(hash) && File.Exists(PathFor(hash));
    }

    public bool Store(SpeechCacheKey key, PcmFormat format, ReadOnlyMemory<byte> pcm)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Store(key.ComputeHash(), format, pcm);
    }

    /// <summary>
    /// Salva audio COMPLETO in modo atomico (scrive .tmp, poi rinomina). Byte spaiati in coda vengono scartati (campioni a 16 bit).
    /// Dopo il salvataggio applica il limite di dimensione. Restituisce falso se non è stato salvato nulla.
    /// </summary>
    public bool Store(string hash, PcmFormat format, ReadOnlyMemory<byte> pcm)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (!IsValidHash(hash)) return false;
        long maxBytes = MaxBytes;
        if (maxBytes <= 0) return false;

        int blockAlign = Math.Max(1, format.Channels * format.BitsPerSample / 8);
        int length = pcm.Length - pcm.Length % blockAlign;
        if (length <= 0) return false;
        if (length + HeaderSize > maxBytes) return false;

        string path = PathFor(hash);
        string temp = path + "." + Guid.NewGuid().ToString("N") + TempExtension;
        lock (_sync)
        {
            try
            {
                EnsureScannedLocked();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                long previous = File.Exists(path) ? new FileInfo(path).Length : 0;
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    Span<byte> header = stackalloc byte[HeaderSize];
                    WriteHeader(header, format);
                    file.Write(header);
                    file.Write(pcm.Span[..length]);
                    file.Flush(true);
                }
                File.Move(temp, path, overwrite: true);
                _sizeBytes += HeaderSize + length - previous;
                EvictLocked(maxBytes);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn($"Cache audio: salvataggio fallito ({ex.GetType().Name}: {ex.Message}).");
                TryDelete(temp);
                return false;
            }
        }
    }

    public long GetSizeBytes()
    {
        lock (_sync)
        {
            try
            {
                EnsureScannedLocked();
            }
            catch (Exception ex)
            {
                _log.Warn($"Cache audio: enumerazione fallita ({ex.GetType().Name}: {ex.Message}).");
            }
            return _sizeBytes;
        }
    }

    /// <summary>Elimina tutto il contenuto della cache (anche i file temporanei).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    foreach (var file in EnumerateFiles(includeTemp: true)) TryDelete(file.FullName);
                    foreach (var sub in System.IO.Directory.EnumerateDirectories(Directory))
                    {
                        try { if (!System.IO.Directory.EnumerateFileSystemEntries(sub).Any()) System.IO.Directory.Delete(sub); }
                        catch (Exception) { /* cartella in uso: resta vuota */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Cache audio: svuotamento fallito ({ex.GetType().Name}: {ex.Message}).");
            }
            _sizeBytes = 0;
            _scanned = true;
            _log.Info("Cache audio svuotata.");
        }
    }

    /// <summary>Forza il rispetto del limite (utile dopo che l'assistente lo ha abbassato).</summary>
    public void Trim()
    {
        lock (_sync)
        {
            try
            {
                EnsureScannedLocked();
                EvictLocked(MaxBytes);
            }
            catch (Exception ex)
            {
                _log.Warn($"Cache audio: riduzione fallita ({ex.GetType().Name}: {ex.Message}).");
            }
        }
    }

    public string PathFor(string hash) => System.IO.Path.Combine(Directory, hash[..2], hash + FileExtension);

    public static bool IsValidHash(string? hash) =>
        hash is { Length: 64 } && hash.All(c => char.IsAsciiHexDigitLower(c));

    internal static void WriteHeader(Span<byte> header, PcmFormat format)
    {
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], format.SampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(header[8..], (short)format.Channels);
        BinaryPrimitives.WriteInt16LittleEndian(header[10..], (short)format.BitsPerSample);
    }

    internal static bool TryParseHeader(ReadOnlySpan<byte> header, [NotNullWhen(true)] out PcmFormat? format)
    {
        format = null;
        if (header.Length < HeaderSize || !header[..4].SequenceEqual(Magic)) return false;
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int channels = BinaryPrimitives.ReadInt16LittleEndian(header[8..]);
        int bits = BinaryPrimitives.ReadInt16LittleEndian(header[10..]);
        if (sampleRate is < 8000 or > 192000 || channels is < 1 or > 2 || bits is not (8 or 16 or 24 or 32)) return false;
        format = new PcmFormat(sampleRate, channels, bits);
        return true;
    }

    private void EnsureScannedLocked()
    {
        if (_scanned) return;
        long total = 0;
        if (System.IO.Directory.Exists(Directory))
        {
            foreach (var file in EnumerateFiles(includeTemp: true))
            {
                if (file.Name.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file.FullName); // residuo di una scrittura interrotta
                    continue;
                }
                total += file.Length;
            }
        }
        _sizeBytes = total;
        _scanned = true;
    }

    private void EvictLocked(long maxBytes)
    {
        if (maxBytes <= 0 || _sizeBytes <= maxBytes) return;
        long target = (long)(maxBytes * EvictionTargetRatio);
        var candidates = EnumerateFiles(includeTemp: false)
            .Select(f => (f.FullName, f.Length, Time: SafeLastWrite(f)))
            .OrderBy(f => f.Time)
            .ToList();
        long total = candidates.Sum(f => f.Length);
        int removed = 0;
        foreach (var (fullName, length, _) in candidates)
        {
            if (total <= target) break;
            if (TryDelete(fullName))
            {
                total -= length;
                removed++;
            }
        }
        _sizeBytes = total;
        if (removed > 0) _log.Info($"Cache audio: eliminati {removed} file meno usati ({total / 1024} KB su {maxBytes / 1024} KB).");
    }

    private IEnumerable<FileInfo> EnumerateFiles(bool includeTemp)
    {
        if (!System.IO.Directory.Exists(Directory)) yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 };
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*", options))
        {
            bool isPcm = path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase);
            bool isTemp = path.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase);
            if (!isPcm && !(includeTemp && isTemp)) continue;
            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) continue; }
            catch (Exception) { continue; }
            yield return info;
        }
    }

    private static DateTime SafeLastWrite(FileInfo file)
    {
        try { return file.LastWriteTimeUtc; } catch (Exception) { return DateTime.MinValue; }
    }

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (Exception) { /* file in uso o sola lettura: il LRU perde una hit, nulla di grave */ }
    }

    private void DeleteCorrupt(string path)
    {
        _log.Warn("Cache audio: file corrotto eliminato.");
        lock (_sync)
        {
            long length = 0;
            try { length = new FileInfo(path).Length; } catch (Exception) { /* già sparito */ }
            if (TryDelete(path) && _scanned) _sizeBytes = Math.Max(0, _sizeBytes - length);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
