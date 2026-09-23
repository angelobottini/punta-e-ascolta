using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Logic.Tests.Fakes;

/// <summary>Registro delle chiamate condiviso fra i finti componenti, per verificare l'ordine delle fasi.</summary>
internal sealed class CallLog
{
    private readonly List<string> _calls = new();

    public void Add(string call)
    {
        lock (_calls) _calls.Add(call);
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_calls) return _calls.ToList();
    }

    public bool Contains(string call) => Snapshot().Contains(call);

    public int Count(string call) => Snapshot().Count(c => c == call);
}

internal sealed class ListLog : ILog
{
    private readonly List<(string Level, string Message)> _entries = new();

    public bool IsDebugEnabled { get; set; }

    public IReadOnlyList<(string Level, string Message)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public void Debug(string message) { if (IsDebugEnabled) Add("DEBUG", message); }
    public void Info(string message) => Add("INFO", message);
    public void Warn(string message) => Add("WARN", message);
    public void Error(string message, Exception? exception = null) => Add("ERROR", message + (exception is null ? "" : " | " + exception.Message));

    private void Add(string level, string message)
    {
        lock (_entries) _entries.Add((level, message));
    }
}

internal sealed class FakeSettingsStore : ISettingsStore
{
    public FakeSettingsStore(AppSettings? settings = null) => Current = settings ?? new AppSettings();

    public string FilePath => "settings.json";
    public string DataDirectory => ".";
    public AppSettings Current { get; set; }
    public event Action<AppSettings>? Changed;
    public AppSettings Load() => Current;

    public void Save(AppSettings settings)
    {
        Current = settings;
        Changed?.Invoke(settings);
    }
}

internal sealed class FakeUi : IUiTextSource
{
    private readonly CallLog _log;

    public FakeUi(CallLog log) => _log = log;

    public Func<CancellationToken, Task<UiSelectionInfo?>> Selection { get; set; } = _ => Task.FromResult<UiSelectionInfo?>(null);
    public Func<ScreenPoint, CancellationToken, Task<UiElementInfo?>> Element { get; set; } = (_, _) => Task.FromResult<UiElementInfo?>(null);
    public Func<ScreenPoint, CancellationToken, Task<UiTooltipInfo?>> Tooltip { get; set; } = (_, _) => Task.FromResult<UiTooltipInfo?>(null);

    public Task<UiSelectionInfo?> GetSelectionAsync(CancellationToken ct)
    {
        _log.Add("selezione");
        return Selection(ct);
    }

    public Task<UiElementInfo?> GetElementAtAsync(ScreenPoint point, CancellationToken ct)
    {
        _log.Add("elemento");
        return Element(point, ct);
    }

    public Task<UiTooltipInfo?> FindTooltipAsync(ScreenPoint point, CancellationToken ct)
    {
        _log.Add("suggerimento");
        return Tooltip(point, ct);
    }

    public void Dispose() { }
}

/// <summary>Cattura finta: immagine vuota delle dimensioni richieste (scala 1:1), eventualmente ritagliata su un "monitor".</summary>
internal sealed class FakeCapture : IScreenCapture
{
    private readonly CallLog _log;
    private readonly List<ScreenRect> _requests = new();

    public FakeCapture(CallLog log) => _log = log;

    public double Dpi { get; set; } = 1.0;
    public ScreenRect? Monitor { get; set; }

    public IReadOnlyList<ScreenRect> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public CapturedImage Capture(ScreenRect desired, ScreenPoint anchor)
    {
        _log.Add("cattura");
        lock (_requests) _requests.Add(desired);
        var rect = Monitor is { } m ? desired.Intersect(m) : desired;
        return new CapturedImage(new byte[Math.Max(0, rect.Width * rect.Height * 4)], rect.Width, rect.Height, rect, Dpi);
    }

    public double GetDpiScale(ScreenPoint point) => Dpi;
}

internal class FakeOcr : IOcrEngine
{
    protected readonly CallLog Log;
    private readonly List<CapturedImage> _images = new();

    public FakeOcr(string name, CallLog log)
    {
        Name = name;
        Log = log;
        Handler = (_, _) => Task.FromResult(OcrResult.Empty(Name));
    }

    public string Name { get; }
    public bool IsAvailable { get; set; } = true;
    public Func<CapturedImage, CancellationToken, Task<OcrResult>> Handler { get; set; }

    public IReadOnlyList<CapturedImage> Images
    {
        get { lock (_images) return _images.ToList(); }
    }

    public Task<OcrResult> RecognizeAsync(CapturedImage image, CancellationToken ct)
    {
        Log.Add(Name);
        lock (_images) _images.Add(image);
        return Handler(image, ct);
    }

    /// <summary>Risultato fisso.</summary>
    public FakeOcr Returns(params OcrLine[] lines)
    {
        Handler = (_, _) => Task.FromResult(new OcrResult(lines, Name, 5));
        return this;
    }

    public void Dispose() { }
}

/// <summary>Motore con il passaggio mirato attorno al punto (come il motore Windows).</summary>
internal sealed class FakePointOcr : FakeOcr, IPointOcrEngine
{
    private readonly List<(double X, double Y)> _points = new();

    public FakePointOcr(string name, CallLog log) : base(name, log)
    {
        PointHandler = (_, _, _, _) => Task.FromResult(OcrResult.Empty(Name));
    }

    public Func<CapturedImage, double, double, CancellationToken, Task<OcrResult>> PointHandler { get; set; }

    public IReadOnlyList<(double X, double Y)> Points
    {
        get { lock (_points) return _points.ToList(); }
    }

    public FakePointOcr PointReturns(params OcrLine[] lines)
    {
        PointHandler = (_, _, _, _) => Task.FromResult(new OcrResult(lines, Name, 5));
        return this;
    }

    public Task<OcrResult> RecognizeAroundPointAsync(CapturedImage image, double x, double y, CancellationToken ct)
    {
        Log.Add(Name + ":mirato");
        lock (_points) _points.Add((x, y));
        return PointHandler(image, x, y, ct);
    }
}

internal sealed class FakeClipboard : IClipboardSelectionReader
{
    private readonly CallLog _log;

    public FakeClipboard(CallLog log) => _log = log;

    public string? Text { get; set; }

    /// <summary>Se impostato sostituisce <see cref="Text"/> (per simulare un ripiego lento o che fallisce).</summary>
    public Func<CancellationToken, Task<string?>>? Behaviour { get; set; }

    public Task<string?> TryCopySelectionAsync(CancellationToken ct)
    {
        _log.Add("appunti");
        return Behaviour?.Invoke(ct) ?? Task.FromResult(Text);
    }
}

/// <summary>Servizio vocale finto: registra le richieste; con <see cref="BlockUntilStopped"/> "parla" finché non arriva Stop o l'annullamento.</summary>
internal sealed class FakeSpeech : ISpeechService
{
    private readonly object _gate = new();
    private readonly List<SpeechRequest> _requests = new();
    private readonly SemaphoreSlim _started = new(0);
    private TaskCompletionSource? _current;
    private int _stopCount;

    public bool BlockUntilStopped { get; set; }

    /// <summary>Se impostata, SpeakAsync la lancia dopo aver registrato la richiesta.</summary>
    public Exception? ThrowOnSpeak { get; set; }

    /// <summary>Simula una voce che parla per conto d'altri (es. rilettura della dettatura).</summary>
    public bool ExternalSpeaking { get; set; }

    public bool IsSpeaking
    {
        get { lock (_gate) return ExternalSpeaking || _current is not null; }
    }

    public int StopCount => Volatile.Read(ref _stopCount);

    public IReadOnlyList<SpeechRequest> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    public event Action<bool>? SpeakingChanged;

    public async Task SpeakAsync(SpeechRequest request, CancellationToken ct)
    {
        TaskCompletionSource? tcs = null;
        lock (_gate)
        {
            _requests.Add(request);
            if (BlockUntilStopped)
            {
                _current?.TrySetResult();
                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _current = tcs;
            }
        }
        _started.Release();
        if (ThrowOnSpeak is { } error) throw error;
        if (tcs is null) return;
        SpeakingChanged?.Invoke(true);
        using (ct.Register(() => tcs.TrySetResult()))
        {
            await tcs.Task.ConfigureAwait(false);
        }
        lock (_gate)
        {
            if (_current == tcs) _current = null;
        }
        SpeakingChanged?.Invoke(false);
    }

    public void Stop()
    {
        Interlocked.Increment(ref _stopCount);
        lock (_gate)
        {
            ExternalSpeaking = false;
            _current?.TrySetResult();
            _current = null;
        }
    }

    /// <summary>Attende che inizi una lettura (una per chiamata).</summary>
    public Task<bool> WaitStartedAsync(int timeoutMs = 5000) => _started.WaitAsync(timeoutMs);

    public void Dispose() { }
}

internal sealed class FakeDictation : IDictationService
{
    private int _toggles;

    public int Toggles => Volatile.Read(ref _toggles);
    public DictationState State { get; set; } = DictationState.Idle;
    public event Action<DictationState>? StateChanged;

    public Task ToggleAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _toggles);
        StateChanged?.Invoke(DictationState.Recording);
        return Task.CompletedTask;
    }

    public void Cancel() { }
    public void Dispose() { }
}

/// <summary>Risolutore finto per l'orchestratore: registra richieste e token.</summary>
internal sealed class FakeResolver : ITextResolver
{
    private readonly object _gate = new();
    private readonly List<(ReadRequest Request, CancellationToken Token)> _calls = new();
    private readonly SemaphoreSlim _called = new(0);

    public Func<ReadRequest, CancellationToken, Task<ReadOutcome>> Handler { get; set; } =
        (_, _) => Task.FromResult(Text("Ciao."));

    public IReadOnlyList<(ReadRequest Request, CancellationToken Token)> Calls
    {
        get { lock (_gate) return _calls.ToList(); }
    }

    public Task<ReadOutcome> ResolveAsync(ReadRequest request, CancellationToken ct)
    {
        lock (_gate) _calls.Add((request, ct));
        _called.Release();
        return Handler(request, ct);
    }

    public Task<bool> WaitCalledAsync(int timeoutMs = 5000) => _called.WaitAsync(timeoutMs);

    public static ReadOutcome Text(string text, SpeechKind kind = SpeechKind.Sentence, string? language = "it", bool sensitive = false) =>
        new(ReadSource.UiaSentence, text, kind, language, sensitive, 3, "finto");
}

/// <summary>Orologio finto: i timer scattano solo con <see cref="Advance"/>, sul thread del test.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _now.UtcTicks;
    }

    public int ActiveTimers
    {
        get { lock (_gate) return _timers.Count(t => t.DueAt is not null); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt is { } at && at <= _now).OrderBy(t => t.DueAt).ToList();
        }
        foreach (var timer in due) timer.Fire();
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate) _timers.Remove(timer);
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;

        public FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Fire()
        {
            if (DueAt is null) return;
            DueAt = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : DueAt + _period;
            _callback(_state);
        }

        public void Dispose()
        {
            DueAt = null;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Costruttori di righe OCR: parole di 8 px per carattere separate da 6 px.</summary>
internal static class Ocr
{
    public static OcrLine Line(string text, double x, double y, double h = 16)
    {
        var words = new List<OcrWord>();
        double cx = x;
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            double width = w.Length * 8;
            words.Add(new OcrWord(w, new ImageRect(cx, y, width, h)));
            cx += width + 6;
        }
        var box = new ImageRect(x, y, words[^1].Box.Right - x, h);
        return new OcrLine(text, box, words);
    }
}
