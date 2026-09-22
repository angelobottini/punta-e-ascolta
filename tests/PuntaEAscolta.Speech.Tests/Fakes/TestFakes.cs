using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Speech.Tests.Fakes;

internal static class TestKeys
{
    /// <summary>Chiave finta: nessun log deve mai contenerla.</summary>
    public const string ApiKey = "sk_test_SEGRETISSIMA_0123456789abcdef";
    public const string VoiceId = "voce_it_123";
}

internal static class TestUtil
{
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condizione non raggiunta: " + (what ?? "?"));
            await Task.Delay(5);
        }
    }

    public static Task<T> Bounded<T>(this Task<T> task, int timeoutMs = 10000) => task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    public static Task Bounded(this Task task, int timeoutMs = 10000) => task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

    /// <summary>PCM finto riconoscibile: i byte UTF-8 di un'etichetta, portati a lunghezza pari (campioni a 16 bit).</summary>
    public static byte[] Pcm(string label)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(label);
        if (bytes.Length % 2 == 1) bytes = [.. bytes, (byte)'#'];
        return bytes;
    }

    public static string Text(byte[] pcm) => Encoding.UTF8.GetString(pcm).TrimEnd('#');

    public static byte[] Repeat(byte value, int count) => Enumerable.Repeat(value, count).ToArray();
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pea-speech-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (Exception) { /* file ancora aperti: pazienza */ }
    }
}

internal sealed class FakeSettingsStore : ISettingsStore
{
    public FakeSettingsStore(AppSettings? settings = null, string? dataDirectory = null)
    {
        Current = settings ?? new AppSettings();
        DataDirectory = dataDirectory ?? System.IO.Path.GetTempPath();
    }

    public string FilePath => System.IO.Path.Combine(DataDirectory, "settings.json");
    public string DataDirectory { get; }
    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? Changed;
    public AppSettings Load() => Current;

    public void Save(AppSettings settings)
    {
        Current = settings;
        Changed?.Invoke(settings);
    }

    /// <summary>Impostazioni con chiave e voce ElevenLabs configurate.</summary>
    public static FakeSettingsStore WithElevenLabs(Action<AppSettings>? tweak = null)
    {
        var settings = new AppSettings();
        settings.Speech.ElevenLabsProtectedApiKey = FakeProtector.Prefix + TestKeys.ApiKey;
        settings.Speech.ElevenLabsVoiceId = TestKeys.VoiceId;
        tweak?.Invoke(settings);
        return new FakeSettingsStore(settings);
    }
}

internal sealed class FakeProtector : ISecretProtector
{
    public const string Prefix = "enc:";
    public string Protect(string plainText) => Prefix + plainText;
    public string? Unprotect(string protectedText) => protectedText.StartsWith(Prefix, StringComparison.Ordinal) ? protectedText[Prefix.Length..] : null;
}

internal sealed class CollectingLog : ILog
{
    private readonly ConcurrentQueue<string> _entries = new();

    public bool IsDebugEnabled { get; set; } = true;
    public IReadOnlyList<string> Entries => _entries.ToArray();

    public void Debug(string message) => _entries.Enqueue("DEBUG " + message);
    public void Info(string message) => _entries.Enqueue("INFO " + message);
    public void Warn(string message) => _entries.Enqueue("WARN " + message);
    public void Error(string message, Exception? exception = null) =>
        _entries.Enqueue("ERROR " + message + (exception is null ? "" : " | " + exception));

    public bool Contains(string fragment) => _entries.Any(e => e.Contains(fragment, StringComparison.Ordinal));
}

/// <summary>Orologio manuale: il tempo avanza solo con Advance, e con lui i timer (Task.Delay, WaitAsync, CancelAfter).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public int ActiveTimers
    {
        get { lock (_lock) return _timers.Count(t => t.Due is not null); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_lock) _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_lock) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.Due is { } due && due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                if (next.Due > _now) _now = next.Due!.Value;
                next.Due = next.Period is { } p && p > TimeSpan.Zero ? _now + p : null;
            }
            next.Fire();
        }
    }

    internal DateTimeOffset Now
    {
        get { lock (_lock) return _now; }
    }

    internal void Remove(ManualTimer timer)
    {
        lock (_lock) _timers.Remove(timer);
    }

    internal sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }
        public TimeSpan? Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._lock) Due = null;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// HTTP finto
// ---------------------------------------------------------------------------------------------------------------------

internal sealed record CapturedPart(string Name, string? FileName, string? ContentType, byte[] Data)
{
    public string Text => Encoding.UTF8.GetString(Data);
}

internal sealed class CapturedRequest
{
    public required HttpMethod Method { get; init; }
    public required Uri Uri { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public string? Body { get; init; }
    public List<CapturedPart> Parts { get; init; } = new();

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    public CapturedPart? Part(string name) => Parts.FirstOrDefault(p => p.Name == name);
}

internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, int, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();
    private int _count;

    public FakeHttpHandler(Func<CapturedRequest, int, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public FakeHttpHandler(Func<CapturedRequest, int, HttpResponseMessage> responder)
        : this((request, index, _) => Task.FromResult(responder(request, index)))
    {
    }

    public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();
    public int Count => Volatile.Read(ref _count);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers) headers[header.Key] = string.Join(",", header.Value);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers) headers[header.Key] = string.Join(",", header.Value);
        }

        string? body = null;
        var parts = new List<CapturedPart>();
        if (request.Content is MultipartFormDataContent multipart)
        {
            foreach (var part in multipart)
            {
                var disposition = part.Headers.ContentDisposition;
                parts.Add(new CapturedPart(
                    disposition?.Name?.Trim('"') ?? "",
                    disposition?.FileName?.Trim('"'),
                    part.Headers.ContentType?.MediaType,
                    await part.ReadAsByteArrayAsync(cancellationToken)));
            }
        }
        else if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var captured = new CapturedRequest
        {
            Method = request.Method,
            Uri = request.RequestUri!,
            Headers = headers,
            Body = body,
            Parts = parts
        };
        _requests.Enqueue(captured);
        int index = Interlocked.Increment(ref _count) - 1;
        return await _responder(captured, index, cancellationToken);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Audio(byte[] pcm)
    {
        var content = new ByteArrayContent(pcm);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        response.Headers.TryAddWithoutValidation("character-cost", "5");
        return response;
    }

    public static HttpResponseMessage Streaming(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

/// <summary>Stream "di rete" pilotato dalla prova: i dati arrivano quando la prova li spinge.</summary>
internal sealed class ControlledStream : Stream
{
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
    private byte[]? _pending;
    private int _offset;
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public void Push(byte[] data) => _channel.Writer.TryWrite(data);
    public void Complete() => _channel.Writer.TryComplete();
    public void Fail(Exception error) => _channel.Writer.TryComplete(error);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pending is null || _offset >= _pending.Length)
        {
            bool more;
            try
            {
                more = await _channel.Reader.WaitToReadAsync(cancellationToken);
            }
            catch (ChannelClosedException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
            if (!more || !_channel.Reader.TryRead(out _pending)) return 0;
            _offset = 0;
        }
        int count = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Interlocked.Exchange(ref _disposed, 1);
        base.Dispose(disposing);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Audio finto
// ---------------------------------------------------------------------------------------------------------------------

internal sealed class PlayedAudio
{
    private readonly MemoryStream _bytes = new();

    public PlayedAudio(PcmFormat format, double volume, long startedTimestamp)
    {
        Format = format;
        Volume = volume;
        StartedTimestamp = startedTimestamp;
    }

    public PcmFormat Format { get; }
    public double Volume { get; }
    public long StartedTimestamp { get; }
    public bool Completed { get; set; }
    public bool Interrupted { get; set; }

    public byte[] Bytes
    {
        get { lock (_bytes) return _bytes.ToArray(); }
    }

    public int Length
    {
        get { lock (_bytes) return (int)_bytes.Length; }
    }

    public string Text => TestUtil.Text(Bytes);

    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_bytes) _bytes.Write(data);
    }
}

/// <summary>Lettore finto: registra i byte letti, rispetta l'annullamento (solleva OCE) e Stop (ritorna senza eccezioni).</summary>
internal sealed class FakePlayer : IAudioPlayer
{
    private readonly ConcurrentQueue<PlayedAudio> _played = new();
    private CancellationTokenSource? _stopCts;
    private int _playing;

    /// <summary>Pausa dopo ogni blocco letto (simula il tempo reale).</summary>
    public int ReadDelayMs { get; set; }
    public int ReadSize { get; set; } = 4096;

    /// <summary>Richiamato all'avvio di ogni PlayAsync, prima di leggere.</summary>
    public Action<PlayedAudio>? OnPlayStarted { get; set; }

    public Exception? ThrowOnPlay { get; set; }

    public IReadOnlyList<PlayedAudio> Played => _played.ToArray();
    public int StopCount;
    public bool IsPlaying => Volatile.Read(ref _playing) > 0;

    public async Task PlayAsync(Stream pcm, PcmFormat format, double volume, CancellationToken ct)
    {
        if (ThrowOnPlay is { } error) throw error;
        var entry = new PlayedAudio(format, volume, System.Diagnostics.Stopwatch.GetTimestamp());
        _played.Enqueue(entry);
        OnPlayStarted?.Invoke(entry);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Interlocked.Exchange(ref _stopCts, stop);
        Interlocked.Increment(ref _playing);
        var buffer = new byte[ReadSize];
        try
        {
            while (true)
            {
                int read = await pcm.ReadAsync(buffer, stop.Token);
                if (read <= 0) break;
                entry.Append(buffer.AsSpan(0, read));
                if (ReadDelayMs > 0) await Task.Delay(ReadDelayMs, stop.Token);
            }
            entry.Completed = true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            entry.Interrupted = true;
            if (ct.IsCancellationRequested) throw;
        }
        finally
        {
            Interlocked.Decrement(ref _playing);
            Interlocked.CompareExchange(ref _stopCts, null, stop);
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref StopCount);
        try { Volatile.Read(ref _stopCts)?.Cancel(); } catch (ObjectDisposedException) { /* già finita */ }
    }

    public void Dispose() { }
}

/// <summary>Sintetizzatore finto: per impostazione predefinita restituisce il PCM "prefisso:testo".</summary>
internal sealed class FakeSynthesizer : ISpeechSynthesizer
{
    private readonly ConcurrentQueue<SpeechRequest> _requests = new();

    public FakeSynthesizer(string id, int sampleRate = 22050)
    {
        Id = id;
        SampleRate = sampleRate;
        Behaviour = (request, _) => Task.FromResult(new SpeechAudio(new MemoryStream(TestUtil.Pcm($"{Id}:{request.Text}")), new PcmFormat(SampleRate)));
    }

    public string Id { get; }
    public int SampleRate { get; }
    public bool IsConfigured { get; set; } = true;
    public Func<SpeechRequest, CancellationToken, Task<SpeechAudio>> Behaviour { get; set; }
    public IReadOnlyList<SpeechRequest> Requests => _requests.ToArray();
    public int Count => _requests.Count;

    public Task<SpeechAudio> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
    {
        _requests.Enqueue(request);
        return Behaviour(request, ct);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Dettatura finta
// ---------------------------------------------------------------------------------------------------------------------

internal sealed class FakeRecorder : IAudioRecorder
{
    public List<string?> StartedDevices { get; } = new();
    public int StopCount;
    public string? FailingDevice { get; set; }
    public bool FailAlways { get; set; }
    public byte[] Recording { get; set; } = TestUtil.Repeat(1, 32000);
    public bool IsRecording { get; private set; }

    public IReadOnlyList<AudioInputDevice> GetDevices() => [new AudioInputDevice("mic1", "Cuffie", true)];

    public void Start(string? deviceId)
    {
        lock (StartedDevices) StartedDevices.Add(deviceId);
        if (FailAlways || (deviceId is not null && deviceId == FailingDevice)) throw new InvalidOperationException("Microfono assente");
        IsRecording = true;
    }

    public byte[] Stop()
    {
        Interlocked.Increment(ref StopCount);
        IsRecording = false;
        return Recording;
    }

    public void Dispose() { }
}

internal sealed class FakeSpeechToText : ISpeechToText
{
    private readonly ConcurrentQueue<(byte[] Pcm, string Language)> _calls = new();

    public bool IsConfigured { get; set; } = true;
    public Func<byte[], string, CancellationToken, Task<string>> Behaviour { get; set; } = (_, _, _) => Task.FromResult("ciao mondo");
    public IReadOnlyList<(byte[] Pcm, string Language)> Calls => _calls.ToArray();

    public Task<string> TranscribeAsync(byte[] pcm16kMono, string languageCode, CancellationToken ct)
    {
        _calls.Enqueue((pcm16kMono, languageCode));
        return Behaviour(pcm16kMono, languageCode, ct);
    }

    /// <summary>Restituisce i testi indicati, uno per chiamata.</summary>
    public void Script(params string[] texts)
    {
        var queue = new ConcurrentQueue<string>(texts);
        Behaviour = (_, _, _) => Task.FromResult(queue.TryDequeue(out var text) ? text : "");
    }
}

internal sealed class FakeInjector : ITextInjector
{
    private readonly ConcurrentQueue<string> _actions = new();

    public IReadOnlyList<string> Actions => _actions.ToArray();

    public Task TypeTextAsync(string text, CancellationToken ct)
    {
        _actions.Enqueue("type:" + text);
        return Task.CompletedTask;
    }

    public Task PressEnterAsync(CancellationToken ct)
    {
        _actions.Enqueue("enter");
        return Task.CompletedTask;
    }

    public Task PressBackspaceAsync(int count, CancellationToken ct)
    {
        _actions.Enqueue("backspace:" + count);
        return Task.CompletedTask;
    }
}

/// <summary>Servizio vocale finto per la dettatura: registra le richieste; le frasi possono restare "in lettura" fino a Stop.</summary>
internal sealed class FakeSpeechService : ISpeechServiceWithOutcome
{
    private readonly ConcurrentQueue<SpeechRequest> _spoken = new();
    private TaskCompletionSource<SpeechOutcome>? _blocking;

    public bool BlockSentences { get; set; }
    public SpeechOutcome? ForcedSentenceOutcome { get; set; }
    public int StopCount;

    public IReadOnlyList<SpeechRequest> Spoken => _spoken.ToArray();
    public IReadOnlyList<string> Texts => _spoken.Select(r => r.Text).ToArray();
    public bool IsBlocking => Volatile.Read(ref _blocking) is not null;
    public bool IsSpeaking => IsBlocking;
    public event Action<bool>? SpeakingChanged;

    public Task SpeakAsync(SpeechRequest request, CancellationToken ct) => SpeakWithOutcomeAsync(request, ct);

    public async Task<SpeechOutcome> SpeakWithOutcomeAsync(SpeechRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _spoken.Enqueue(request);
        if (request.Kind == SpeechKind.Sentence)
        {
            if (ForcedSentenceOutcome is { } forced) return forced;
            if (BlockSentences)
            {
                var tcs = new TaskCompletionSource<SpeechOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _blocking, tcs);
                SpeakingChanged?.Invoke(true);
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                {
                    return await tcs.Task;
                }
            }
        }
        return SpeechOutcome.Completed;
    }

    public void Stop()
    {
        Interlocked.Increment(ref StopCount);
        Interlocked.Exchange(ref _blocking, null)?.TrySetResult(SpeechOutcome.Stopped);
    }

    /// <summary>Simula un'altra lettura che sostituisce la rilettura in corso.</summary>
    public void Supersede() => Interlocked.Exchange(ref _blocking, null)?.TrySetResult(SpeechOutcome.Superseded);

    /// <summary>Fa finire normalmente la rilettura in corso.</summary>
    public void FinishSentence() => Interlocked.Exchange(ref _blocking, null)?.TrySetResult(SpeechOutcome.Completed);

    public void Dispose() { }
}
