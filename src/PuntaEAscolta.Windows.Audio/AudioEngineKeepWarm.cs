using NAudio.CoreAudioApi;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Audio;

/// <summary>
/// Tiene "caldo" il motore audio di Windows: uno stream WASAPI condiviso inizializzato e MAI avviato sul
/// dispositivo di uscita in uso. Nessun suono, nessun thread, nessuna elaborazione; basta la sua esistenza
/// perché l'apertura di un nuovo dispositivo per la lettura successiva costi circa 20 ms invece di 200-300 ms
/// (misurato su Snapdragon X). Si rilascia da solo dopo un periodo senza riproduzioni e si ricrea quando
/// cambia il dispositivo predefinito. Tutti i metodi sono sicuri da qualsiasi thread e non lanciano eccezioni.
/// </summary>
internal sealed class AudioEngineKeepWarm : IDisposable
{
    /// <summary>Durata del buffer dello stream caldo (100 ns): irrilevante, lo stream non parte mai.</summary>
    private const long BufferDuration100ns = 1_000_000;

    private readonly ILog _log;
    private readonly object _lock = new();
    private readonly Timer _idleTimer;

    private TimeSpan _duration;
    private MMDevice? _device;
    private AudioClient? _client;
    private string? _deviceId;
    private string? _pendingId;
    private bool _disposed;

    public AudioEngineKeepWarm(ILog log, TimeSpan duration)
    {
        _log = log;
        _duration = duration;
        _idleTimer = new Timer(static state => ((AudioEngineKeepWarm)state!).OnIdle(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Per quanto tempo dopo l'ultima riproduzione resta aperto lo stream caldo. Zero lo disattiva e lo rilascia.</summary>
    public TimeSpan Duration
    {
        get
        {
            lock (_lock)
            {
                return _duration;
            }
        }

        set
        {
            bool release;
            lock (_lock)
            {
                _duration = value < TimeSpan.Zero ? TimeSpan.Zero : value;
                release = _duration == TimeSpan.Zero;
                if (!release && _client is not null)
                {
                    ScheduleIdle();
                }
            }

            if (release)
            {
                Release("disattivato");
            }
        }
    }

    /// <summary>Vero se lo stream caldo è aperto (per diagnostica e prove).</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _client is not null;
            }
        }
    }

    /// <summary>
    /// Da chiamare quando si è appena aperto un dispositivo per una lettura: rinnova il periodo di attesa e,
    /// se serve, crea lo stream caldo sullo stesso dispositivo. La creazione avviene sul pool di thread.
    /// </summary>
    public void Touch(string deviceId)
    {
        lock (_lock)
        {
            if (_disposed || _duration == TimeSpan.Zero || string.IsNullOrEmpty(deviceId))
            {
                return;
            }

            ScheduleIdle();
            if (_client is not null && string.Equals(_deviceId, deviceId, StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(_pendingId, deviceId, StringComparison.Ordinal))
            {
                return;
            }

            _pendingId = deviceId;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static state => state.self.Create(state.id), (self: this, id: deviceId), preferLocal: false);
    }

    /// <summary>Rinnova il periodo di attesa senza creare nulla (fine di una lettura).</summary>
    public void Renew()
    {
        lock (_lock)
        {
            if (!_disposed && _client is not null && _duration > TimeSpan.Zero)
            {
                ScheduleIdle();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _idleTimer.Dispose();
        Release(null);
    }

    private void ScheduleIdle()
    {
        try
        {
            _idleTimer.Change(_duration, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Chiusura in corso: nulla da pianificare.
        }
    }

    private void OnIdle()
    {
        Release("inattività");
    }

    private void Create(string deviceId)
    {
        MMDevice? device = null;
        AudioClient? client = null;
        try
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                device = enumerator.GetDevice(deviceId);
            }

            client = device.AudioClient;
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, BufferDuration100ns, 0, client.MixFormat, Guid.Empty);
        }
        catch (Exception ex)
        {
            _log.Warn($"Audio: stream di mantenimento non disponibile ({ex.GetType().Name}: {ex.Message}).");
            SafeDispose(client, device);
            lock (_lock)
            {
                if (string.Equals(_pendingId, deviceId, StringComparison.Ordinal))
                {
                    _pendingId = null;
                }
            }

            return;
        }

        AudioClient? oldClient = null;
        MMDevice? oldDevice = null;
        bool keep;
        lock (_lock)
        {
            keep = !_disposed && _duration > TimeSpan.Zero && string.Equals(_pendingId, deviceId, StringComparison.Ordinal);
            if (string.Equals(_pendingId, deviceId, StringComparison.Ordinal))
            {
                _pendingId = null;
            }

            if (keep)
            {
                oldClient = _client;
                oldDevice = _device;
                _client = client;
                _device = device;
                _deviceId = deviceId;
                ScheduleIdle();
            }
        }

        if (!keep)
        {
            SafeDispose(client, device);
            return;
        }

        SafeDispose(oldClient, oldDevice);
        if (_log.IsDebugEnabled)
        {
            _log.Debug("Audio: stream di mantenimento aperto.");
        }
    }

    private void Release(string? reason)
    {
        AudioClient? client;
        MMDevice? device;
        lock (_lock)
        {
            client = _client;
            device = _device;
            _client = null;
            _device = null;
            _deviceId = null;
        }

        if (client is null && device is null)
        {
            return;
        }

        SafeDispose(client, device);
        if (reason is not null && _log.IsDebugEnabled)
        {
            _log.Debug($"Audio: stream di mantenimento chiuso ({reason}).");
        }
    }

    private void SafeDispose(AudioClient? client, MMDevice? device)
    {
        try
        {
            client?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"Audio: errore nella chiusura dello stream di mantenimento ({ex.GetType().Name}).");
        }

        try
        {
            device?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"Audio: errore nel rilascio del dispositivo ({ex.GetType().Name}).");
        }
    }
}
