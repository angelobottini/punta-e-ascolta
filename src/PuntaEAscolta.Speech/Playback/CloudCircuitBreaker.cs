using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Speech.Playback;

/// <summary>
/// Interruttore automatico del fornitore cloud. Regole:
/// chiave rifiutata (InvalidKey) o senza il permesso di sintesi (MissingPermission) → cloud sospeso finché le impostazioni
/// non cambiano (ogni richiesta fallirebbe uguale);
/// crediti esauriti (QuotaExceeded) → sospeso 30 minuti;
/// 3 errori consecutivi di rete, tempo o server (anche Configuration, che si ripete uguale a ogni richiesta) →
/// sospeso 60 s; se la prima prova dopo la sospensione fallisce di nuovo → 5 minuti, e così via finché una richiesta riesce;
/// troppe richieste (RateLimited) e NotConfigured → nessuna sospensione, si ripiega solo per la lettura corrente.
/// Durante la sospensione non si fanno tentativi in linea: si usa subito la voce locale.
/// </summary>
internal sealed class CloudCircuitBreaker
{
    public const int TransientThreshold = 3;
    public static readonly TimeSpan FirstTransientPause = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RepeatedTransientPause = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan QuotaPause = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly object _sync = new();
    private int _consecutiveTransient;
    private bool _escalated;
    private bool _untilSettingsChange;
    private DateTimeOffset _suspendedUntil = DateTimeOffset.MinValue;

    public CloudCircuitBreaker(TimeProvider time, ILog log)
    {
        _time = time;
        _log = log;
    }

    /// <summary>Vero se il cloud può essere interpellato adesso.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return !_untilSettingsChange && _time.GetUtcNow() >= _suspendedUntil;
            }
        }
    }

    /// <summary>Fine della sospensione in corso; DateTimeOffset.MaxValue se attende un cambio delle impostazioni; null se il cloud è disponibile.</summary>
    public DateTimeOffset? SuspendedUntil
    {
        get
        {
            lock (_sync)
            {
                if (_untilSettingsChange) return DateTimeOffset.MaxValue;
                return _time.GetUtcNow() < _suspendedUntil ? _suspendedUntil : null;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_sync)
        {
            if (_escalated) _log.Info("Voce: ElevenLabs risponde di nuovo.");
            _consecutiveTransient = 0;
            _escalated = false;
        }
    }

    /// <param name="missingPermission">Con <see cref="SpeechProviderReason.MissingPermission"/>: il permesso che manca, se noto (solo per il registro).</param>
    public void RecordFailure(SpeechProviderReason reason, string? missingPermission = null)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            switch (reason)
            {
                case SpeechProviderReason.InvalidKey:
                    if (!_untilSettingsChange)
                    {
                        _log.Warn("Voce: chiave ElevenLabs rifiutata; uso la voce di Windows finché le impostazioni non cambiano.");
                    }
                    _untilSettingsChange = true;
                    break;

                case SpeechProviderReason.MissingPermission:
                    if (!_untilSettingsChange)
                    {
                        string which = string.IsNullOrWhiteSpace(missingPermission) ? "di sintesi vocale (text_to_speech)" : missingPermission.Trim();
                        _log.Warn($"Voce: la chiave ElevenLabs non ha il permesso {which}; uso la voce di Windows finché le impostazioni non cambiano. " +
                                  "Sul sito di ElevenLabs attivare \"Text to Speech\" nei permessi della chiave.");
                    }
                    _untilSettingsChange = true;
                    break;

                case SpeechProviderReason.QuotaExceeded:
                    _suspendedUntil = Max(_suspendedUntil, now + QuotaPause);
                    _log.Warn($"Voce: crediti ElevenLabs esauriti; uso la voce di Windows per {QuotaPause.TotalMinutes:F0} minuti.");
                    break;

                case SpeechProviderReason.Network:
                case SpeechProviderReason.Timeout:
                case SpeechProviderReason.Server:
                case SpeechProviderReason.Configuration:
                    if (now < _suspendedUntil) return; // richiesta partita prima della sospensione: non la allunga
                    _consecutiveTransient++;
                    if (_escalated)
                    {
                        Trip(now, RepeatedTransientPause, reason);
                    }
                    else if (_consecutiveTransient >= TransientThreshold)
                    {
                        Trip(now, FirstTransientPause, reason);
                        _escalated = true;
                    }
                    break;

                default:
                    // RateLimited, NotConfigured: solo ripiego per questa lettura.
                    break;
            }
        }
    }

    /// <summary>Le impostazioni sono cambiate: si riparte da zero (nuova chiave, nuova voce, crediti ricaricati...).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            bool wasSuspended = _untilSettingsChange || _time.GetUtcNow() < _suspendedUntil;
            _untilSettingsChange = false;
            _suspendedUntil = DateTimeOffset.MinValue;
            _consecutiveTransient = 0;
            _escalated = false;
            if (wasSuspended) _log.Info("Voce: impostazioni cambiate, ElevenLabs di nuovo disponibile.");
        }
    }

    private void Trip(DateTimeOffset now, TimeSpan pause, SpeechProviderReason reason)
    {
        _suspendedUntil = now + pause;
        _consecutiveTransient = 0;
        _log.Warn($"Voce: ElevenLabs sospeso per {pause.TotalSeconds:F0} s dopo errori ripetuti (ultimo: {reason}); uso la voce di Windows.");
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
