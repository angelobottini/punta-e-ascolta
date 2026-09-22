namespace PuntaEAscolta.Core.Abstractions;

public sealed record AudioInputDevice(string Id, string Name, bool IsDefault);

/// <summary>Registrazione dal microfono in PCM 16 kHz mono 16 bit.</summary>
public interface IAudioRecorder : IDisposable
{
    IReadOnlyList<AudioInputDevice> GetDevices();
    bool IsRecording { get; }
    void Start(string? deviceId);

    /// <summary>Ferma e restituisce il PCM registrato (16 kHz, mono, 16 bit).</summary>
    byte[] Stop();
}

public interface ISpeechToText
{
    bool IsConfigured { get; }
    Task<string> TranscribeAsync(byte[] pcm16kMono, string languageCode, CancellationToken ct);
}

/// <summary>Inserisce testo nell'app in primo piano senza usare gli appunti (Windows: SendInput con KEYEVENTF_UNICODE).</summary>
public interface ITextInjector
{
    Task TypeTextAsync(string text, CancellationToken ct);
    Task PressEnterAsync(CancellationToken ct);
    Task PressBackspaceAsync(int count, CancellationToken ct);
}

public enum DictationState { Idle, Recording, Transcribing, ReadingBack }

public interface IDictationService : IDisposable
{
    DictationState State { get; }
    event Action<DictationState>? StateChanged;

    /// <summary>Primo richiamo: inizia a registrare. Secondo richiamo: ferma, trascrive, rilegge e inserisce.</summary>
    Task ToggleAsync(CancellationToken ct);
    void Cancel();
}
