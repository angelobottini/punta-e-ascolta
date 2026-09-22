using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Logic.Reading;

/// <summary>Evento interno: il timer della pressione lunga è scattato per il DOWN con il progressivo indicato.</summary>
internal sealed record LongPressElapsedEvent(long PendingId, long TimestampMs) : InputEvent(TimestampMs);

/// <summary>
/// Riconoscitore dei gesti del pulsante attivatore (DESIGN.md 3.1, docs/research/input-capture.md sez. 4).
/// - LongPressEnabled = false: si agisce subito sul DOWN, l'UP è ignorato.
/// - LongPressEnabled = true: il clic breve si decide sull'UP; la pressione lunga scatta dal timer a LongPressMs
///   senza aspettare l'UP (che poi non produce nulla).
/// - Anti-rimbalzo: un DOWN entro DebounceMs dall'ultima attivazione è ignorato.
/// - Un UP senza DOWN in sospeso (evento iniettato, duplicato, DOWN scartato) è ignorato.
/// Il punto usato è SEMPRE quello del DOWN. Non è thread-safe: va usato da un solo thread (il worker dell'orchestratore);
/// il timer non tocca lo stato ma notifica tramite <c>onLongPressElapsed</c>, che l'orchestratore riaccoda.
/// </summary>
internal sealed class GestureRecognizer : IDisposable
{
    private readonly Func<InputSettings> _settings;
    private readonly TimeProvider _time;
    private readonly Action<long> _onLongPressElapsed;
    private readonly Action<ScreenPoint, bool> _activate;
    private readonly ILog _log;

    private TriggerButtonEvent? _pendingDown;
    private long _pendingId;
    private bool _longFired;
    private long? _lastActivationMs;
    private ITimer? _timer;

    /// <param name="settings">Impostazioni correnti (rilette a ogni evento).</param>
    /// <param name="time">Sorgente dei timer (sostituibile nei test).</param>
    /// <param name="onLongPressElapsed">Chiamato dal thread del timer con il progressivo del DOWN: chi riceve deve richiamare <see cref="OnLongPressElapsed"/> sul proprio thread.</param>
    /// <param name="activate">Attivazione: punto del DOWN e true se pressione lunga.</param>
    public GestureRecognizer(Func<InputSettings> settings, TimeProvider time, Action<long> onLongPressElapsed, Action<ScreenPoint, bool> activate, ILog log)
    {
        _settings = settings;
        _time = time;
        _onLongPressElapsed = onLongPressElapsed;
        _activate = activate;
        _log = log;
    }

    /// <summary>True se c'è un DOWN in attesa dell'UP o del timer.</summary>
    public bool HasPendingPress => _pendingDown is not null;

    public void OnButton(TriggerButtonEvent ev)
    {
        var s = _settings();
        if (ev.IsDown)
        {
            if (_lastActivationMs is { } last && ev.TimestampMs - last < s.DebounceMs)
            {
                _log.Debug($"Clic ignorato dall'anti-rimbalzo ({ev.TimestampMs - last} ms dal precedente)");
                return;
            }

            if (_pendingDown is not null)
            {
                // Un DOWN mentre un altro è in sospeso: l'UP è andato perso. Si scarta il precedente.
                _log.Debug("Nuovo DOWN con pressione in sospeso: la precedente viene scartata");
                ClearPending();
            }

            if (!s.LongPressEnabled)
            {
                Activate(ev, isLong: false);
                return;
            }

            _pendingDown = ev;
            _longFired = false;
            long id = ++_pendingId;
            var due = TimeSpan.FromMilliseconds(Math.Max(50, s.LongPressMs));
            _timer = _time.CreateTimer(_ => _onLongPressElapsed(id), null, due, Timeout.InfiniteTimeSpan);
        }
        else
        {
            if (_pendingDown is not { } down)
            {
                _log.Debug("UP senza DOWN in sospeso: ignorato");
                return;
            }
            bool alreadyLong = _longFired;
            ClearPending();
            if (!alreadyLong) Activate(down, isLong: false);
        }
    }

    /// <summary>Il timer della pressione lunga è scattato (richiamato sul thread del riconoscitore).</summary>
    public void OnLongPressElapsed(long pendingId)
    {
        if (_pendingDown is not { } down || pendingId != _pendingId || _longFired) return;
        _longFired = true;
        DisposeTimer();
        Activate(down, isLong: true);
    }

    public void Dispose() => ClearPending();

    private void Activate(TriggerButtonEvent down, bool isLong)
    {
        _lastActivationMs = down.TimestampMs;
        _activate(down.Point, isLong);
    }

    private void ClearPending()
    {
        DisposeTimer();
        _pendingDown = null;
        _longFired = false;
    }

    private void DisposeTimer()
    {
        var timer = _timer;
        _timer = null;
        if (timer is null) return;
        try { timer.Dispose(); } catch { /* ignorato */ }
    }
}
