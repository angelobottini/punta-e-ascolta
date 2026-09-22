using PuntaEAscolta.Speech.Playback;
using PuntaEAscolta.Speech.Tests.Fakes;
using Xunit;

namespace PuntaEAscolta.Speech.Tests;

public sealed class CloudCircuitBreakerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly CollectingLog _log = new();

    [Fact]
    public void Invalid_key_suspends_until_reset()
    {
        var breaker = new CloudCircuitBreaker(_time, _log);
        breaker.RecordFailure(SpeechProviderReason.InvalidKey);

        Assert.False(breaker.IsAvailable);
        Assert.Equal(DateTimeOffset.MaxValue, breaker.SuspendedUntil);
        _time.Advance(TimeSpan.FromDays(1));
        Assert.False(breaker.IsAvailable);

        breaker.Reset();
        Assert.True(breaker.IsAvailable);
        Assert.Null(breaker.SuspendedUntil);
    }

    [Fact]
    public void Three_transient_failures_suspend_60_seconds_then_a_failed_probe_suspends_5_minutes()
    {
        var breaker = new CloudCircuitBreaker(_time, _log);
        breaker.RecordFailure(SpeechProviderReason.Network);
        breaker.RecordFailure(SpeechProviderReason.Timeout);
        Assert.True(breaker.IsAvailable);
        breaker.RecordFailure(SpeechProviderReason.Server);
        Assert.False(breaker.IsAvailable);

        // Una richiesta partita prima della sospensione non la allunga.
        breaker.RecordFailure(SpeechProviderReason.Network);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(breaker.IsAvailable);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(breaker.IsAvailable);

        // Prima prova dopo la sospensione fallita: 5 minuti.
        breaker.RecordFailure(SpeechProviderReason.Timeout);
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(breaker.IsAvailable);
        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(breaker.IsAvailable);

        // Un successo azzera tutto: servono di nuovo 3 errori, e la pausa torna a 60 s.
        breaker.RecordSuccess();
        breaker.RecordFailure(SpeechProviderReason.Network);
        breaker.RecordFailure(SpeechProviderReason.Network);
        Assert.True(breaker.IsAvailable);
        breaker.RecordFailure(SpeechProviderReason.Network);
        Assert.False(breaker.IsAvailable);
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(breaker.IsAvailable);
    }

    [Fact]
    public void Success_between_failures_resets_the_count()
    {
        var breaker = new CloudCircuitBreaker(_time, _log);
        breaker.RecordFailure(SpeechProviderReason.Network);
        breaker.RecordFailure(SpeechProviderReason.Network);
        breaker.RecordSuccess();
        breaker.RecordFailure(SpeechProviderReason.Network);
        breaker.RecordFailure(SpeechProviderReason.Network);
        Assert.True(breaker.IsAvailable);
    }

    [Fact]
    public void Quota_suspends_30_minutes_and_rate_limit_never_suspends()
    {
        var breaker = new CloudCircuitBreaker(_time, _log);
        for (int i = 0; i < 10; i++) breaker.RecordFailure(SpeechProviderReason.RateLimited);
        breaker.RecordFailure(SpeechProviderReason.NotConfigured);
        Assert.True(breaker.IsAvailable);

        breaker.RecordFailure(SpeechProviderReason.QuotaExceeded);
        Assert.False(breaker.IsAvailable);
        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.False(breaker.IsAvailable);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(breaker.IsAvailable);
    }
}
