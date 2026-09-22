using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Speech.Cache;
using PuntaEAscolta.Speech.Tests.Fakes;
using Xunit;

namespace PuntaEAscolta.Speech.Tests;

public sealed class SpeechCacheTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly CollectingLog _log = new();

    public void Dispose() => _dir.Dispose();

    private static SpeechCacheKey Key(string text, string voice = "v1") =>
        new("elevenlabs", voice, "eleven_flash_v2_5", "it", "pcm_44100", "0.50|0.75|0.00|1.00|1", text);

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    [Fact]
    public void Store_then_open_returns_same_audio_and_format()
    {
        var cache = new SpeechCache(_dir.Path, () => 10, _log);
        byte[] pcm = TestUtil.Repeat(7, 1000);

        Assert.False(cache.TryOpen(Key("Salva"), out _, out _));
        Assert.True(cache.Store(Key("Salva"), new PcmFormat(24000), pcm));

        Assert.True(cache.Contains(Key("Salva")));
        Assert.True(cache.TryOpen(Key("Salva"), out var stream, out var format));
        Assert.Equal(new PcmFormat(24000), format);
        Assert.Equal(pcm, ReadAll(stream));
        Assert.Equal(1000 + SpeechCache.HeaderSize, cache.GetSizeBytes());
    }

    [Fact]
    public void Key_normalizes_spaces_but_keeps_case_and_other_components()
    {
        Assert.Equal(Key("Salva  con\tnome ").ComputeHash(), Key(" Salva con nome").ComputeHash());
        Assert.NotEqual(Key("IVA").ComputeHash(), Key("iva").ComputeHash());
        Assert.NotEqual(Key("Salva").ComputeHash(), Key("Salva", voice: "v2").ComputeHash());
        Assert.Equal(64, Key("x").ComputeHash().Length);
    }

    [Fact]
    public void Odd_trailing_byte_is_dropped_and_disabled_cache_stores_nothing()
    {
        var cache = new SpeechCache(_dir.Path, () => 10, _log);
        Assert.True(cache.Store(Key("a"), new PcmFormat(16000), TestUtil.Repeat(1, 11)));
        Assert.True(cache.TryOpen(Key("a"), out var stream, out _));
        Assert.Equal(10, ReadAll(stream).Length);

        var disabled = new SpeechCache(System.IO.Path.Combine(_dir.Path, "off"), () => 0, _log);
        Assert.False(disabled.Store(Key("b"), new PcmFormat(16000), TestUtil.Repeat(1, 10)));
        Assert.False(disabled.Contains(Key("b")));
    }

    [Fact]
    public void Least_recently_used_files_are_evicted_over_the_limit()
    {
        var cache = new SpeechCache(_dir.Path, () => 1, _log);
        var format = new PcmFormat(44100);
        byte[] block = TestUtil.Repeat(3, 400 * 1024);

        Assert.True(cache.Store(Key("uno"), format, block));
        File.SetLastWriteTimeUtc(cache.PathFor(Key("uno").ComputeHash()), DateTime.UtcNow.AddMinutes(-10));
        Assert.True(cache.Store(Key("due"), format, block));
        File.SetLastWriteTimeUtc(cache.PathFor(Key("due").ComputeHash()), DateTime.UtcNow.AddMinutes(-5));
        Assert.True(cache.Store(Key("tre"), format, block));

        Assert.False(cache.Contains(Key("uno")));
        Assert.True(cache.Contains(Key("tre")));
        Assert.True(cache.GetSizeBytes() <= 1024 * 1024);
    }

    [Fact]
    public void Clear_empties_everything()
    {
        var cache = new SpeechCache(_dir.Path, () => 10, _log);
        cache.Store(Key("a"), new PcmFormat(16000), TestUtil.Repeat(1, 100));
        cache.Store(Key("b"), new PcmFormat(16000), TestUtil.Repeat(1, 100));

        cache.Clear();

        Assert.Equal(0, cache.GetSizeBytes());
        Assert.False(cache.Contains(Key("a")));
        Assert.Empty(Directory.EnumerateFiles(_dir.Path, "*.pcm", SearchOption.AllDirectories));
    }

    [Fact]
    public void Corrupt_file_is_deleted_and_treated_as_missing()
    {
        var cache = new SpeechCache(_dir.Path, () => 10, _log);
        cache.Store(Key("a"), new PcmFormat(16000), TestUtil.Repeat(1, 100));
        string path = cache.PathFor(Key("a").ComputeHash());
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17]);

        Assert.False(cache.TryOpen(Key("a"), out _, out _));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Leftover_temp_files_are_removed_on_first_scan()
    {
        string sub = System.IO.Path.Combine(_dir.Path, "ab");
        Directory.CreateDirectory(sub);
        string temp = System.IO.Path.Combine(sub, "interrotto.pcm.123.tmp");
        File.WriteAllBytes(temp, [1, 2, 3]);
        var cache = new SpeechCache(_dir.Path, () => 10, _log);

        Assert.Equal(0, cache.GetSizeBytes());
        Assert.False(File.Exists(temp));
    }
}
