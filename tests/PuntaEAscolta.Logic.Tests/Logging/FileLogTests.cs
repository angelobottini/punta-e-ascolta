using System.Net;
using System.Net.Http;
using PuntaEAscolta.Logic.Logging;
using Xunit;

namespace PuntaEAscolta.Logic.Tests.Logging;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PuntaEAscoltaTests", "logs-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 9, 22, 10, 30, 0);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignorato */ }
    }

    private FileLog Create(bool debug = false) => new(_dir, () => debug) { Clock = () => _now };

    private string ReadDay(DateTime day)
    {
        var path = Path.Combine(_dir, FileLog.FileNameFor(DateOnly.FromDateTime(day)));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string FakeKey(string prefix, int length) =>
        prefix + string.Concat(Enumerable.Range(0, length).Select(i => "0123456789abcdef"[i % 16]));

    [Fact]
    public void Info_IsWrittenToTheDailyFile()
    {
        using (var log = Create())
        {
            log.Info("Avvio completato");
            log.Warn("Attenzione");
            Assert.EndsWith("punta-e-ascolta-20260922.log", log.CurrentFilePath);
        }

        var text = ReadDay(_now);
        Assert.Contains("INFO  [", text);
        Assert.Contains("Avvio completato", text);
        Assert.Contains("WARN ", text);
        Assert.Contains("2026-09-22 10:30:00.000", text);
    }

    [Fact]
    public void Debug_IsWrittenOnlyWhenEnabled()
    {
        using (var log = Create(debug: false))
        {
            Assert.False(log.IsDebugEnabled);
            log.Debug("testo riservato");
            log.Info("riga");
        }
        Assert.DoesNotContain("testo riservato", ReadDay(_now));

        using (var log = Create(debug: true))
        {
            Assert.True(log.IsDebugEnabled);
            log.Debug("testo di prova");
        }
        Assert.Contains("DEBUG [", ReadDay(_now));
        Assert.Contains("testo di prova", ReadDay(_now));
    }

    [Fact]
    public void DebugPredicateThrowing_MeansDisabled()
    {
        using var log = new FileLog(_dir, () => throw new InvalidOperationException("guasto"));

        Assert.False(log.IsDebugEnabled);
        log.Debug("niente");
    }

    [Fact]
    public void Rotation_KeepsAtMostSevenLogFiles()
    {
        Directory.CreateDirectory(_dir);
        for (int day = 10; day <= 18; day++)
            File.WriteAllText(Path.Combine(_dir, $"punta-e-ascolta-202609{day:D2}.log"), "vecchio");
        File.WriteAllText(Path.Combine(_dir, "punta-e-ascolta-appunti.log"), "non è un registro giornaliero");
        File.WriteAllText(Path.Combine(_dir, "altro.txt"), "estraneo");

        using (var log = Create()) log.Info("oggi");

        var logs = Directory.GetFiles(_dir, "punta-e-ascolta-2*.log").Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(FileLog.MaxFiles, logs.Count);
        Assert.Equal("punta-e-ascolta-20260913.log", logs[0]);
        Assert.Equal("punta-e-ascolta-20260922.log", logs[^1]);
        Assert.True(File.Exists(Path.Combine(_dir, "punta-e-ascolta-appunti.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "altro.txt")));
    }

    [Fact]
    public void DayChange_OpensANewFile()
    {
        using (var log = Create())
        {
            log.Info("primo giorno");
            _now = _now.AddDays(1).Date.AddMinutes(1);
            log.Info("secondo giorno");
        }

        Assert.Contains("primo giorno", ReadDay(new DateTime(2026, 9, 22)));
        Assert.DoesNotContain("secondo giorno", ReadDay(new DateTime(2026, 9, 22)));
        Assert.Contains("secondo giorno", ReadDay(new DateTime(2026, 9, 23)));
    }

    [Fact]
    public void ApiKeys_AreMaskedInMessagesAndExceptions()
    {
        var skKey = FakeKey("sk_", 48);
        var headerKey = FakeKey("", 40);

        using (var log = Create(debug: true))
        {
            log.Info($"Richiesta con xi-api-key: {headerKey}");
            log.Debug($"Chiave letta: {skKey}");
            log.Error("Errore di rete", new HttpRequestException($"Rifiutata la chiave {skKey}", null, HttpStatusCode.Unauthorized));
            log.Error("Errore generico", new InvalidOperationException($"\"xi-api-key\"=\"{headerKey}\""));
        }

        var text = ReadDay(_now);
        Assert.DoesNotContain(skKey, text);
        Assert.DoesNotContain(headerKey, text);
        Assert.Contains("sk_***", text);
        Assert.Contains("xi-api-key: ***", text);
        Assert.Contains("(HTTP 401)", text);
    }

    [Fact]
    public void Mask_LeavesOrdinaryTextAlone()
    {
        Assert.Equal("ore 8:30, sk_breve", FileLog.Mask("ore 8:30, sk_breve"));
        Assert.Equal("", FileLog.Mask(""));
    }

    [Fact]
    public void NeverThrows_WhenTheDirectoryCannotBeCreated()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "un-file");
        File.WriteAllText(file, "x");

        using var log = new FileLog(Path.Combine(file, "logs"), () => true);
        log.Info("uno");
        log.Error("due", new InvalidOperationException("tre"));
        log.Debug("quattro");
        log.Flush();

        Assert.Null(log.CurrentFilePath);
    }

    [Fact]
    public void AfterDispose_WritesAreIgnored()
    {
        var log = Create();
        log.Info("prima");
        log.Dispose();
        log.Info("dopo");
        log.Dispose();

        var text = ReadDay(_now);
        Assert.Contains("prima", text);
        Assert.DoesNotContain("dopo", text);
    }

    [Fact]
    public async Task ConcurrentWrites_AreAllRecorded()
    {
        using (var log = Create())
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                for (int i = 0; i < 50; i++) log.Info($"filo {t} riga {i}");
            })));
        }

        var lines = ReadDay(_now).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(400, lines.Length);
    }
}
