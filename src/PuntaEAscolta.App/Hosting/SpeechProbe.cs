using System.Diagnostics;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.App.Hosting;

/// <summary>
/// Sonda per le prove a riga di comando: registra quando il lettore riceve il primo audio e se è stata usata la voce locale.
/// Non chiude le dipendenze che avvolge (appartengono alla composizione).
/// </summary>
internal sealed class MeasuredAudioPlayer(IAudioPlayer inner) : IAudioPlayer
{
    private long _firstPlayTimestamp;
    private int _playCount;

    public long FirstPlayTimestamp => Interlocked.Read(ref _firstPlayTimestamp);

    public int PlayCount => Volatile.Read(ref _playCount);

    public bool IsPlaying => inner.IsPlaying;

    public Task PlayAsync(Stream pcm, PcmFormat format, double volume, CancellationToken ct)
    {
        Interlocked.Increment(ref _playCount);
        Interlocked.CompareExchange(ref _firstPlayTimestamp, Stopwatch.GetTimestamp(), 0);
        return inner.PlayAsync(pcm, format, volume, ct);
    }

    public void Stop() => inner.Stop();

    public void Reset()
    {
        Interlocked.Exchange(ref _firstPlayTimestamp, 0);
        Interlocked.Exchange(ref _playCount, 0);
    }

    public void Dispose()
    {
        // Il lettore vero appartiene alla composizione.
    }
}

/// <summary>Conta le sintesi della voce locale (per sapere se la lettura è passata dalla voce di Windows).</summary>
internal sealed class MeasuredSynthesizer(ISpeechSynthesizer inner) : ISpeechSynthesizer
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public string Id => inner.Id;

    public bool IsConfigured => inner.IsConfigured;

    public Task<SpeechAudio> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return inner.SynthesizeAsync(request, ct);
    }

    public void Reset() => Interlocked.Exchange(ref _calls, 0);
}
