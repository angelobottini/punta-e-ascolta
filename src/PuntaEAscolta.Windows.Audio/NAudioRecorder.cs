using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Audio;

/// <summary>
/// Registrazione dal microfono con WASAPI (modalità condivisa, formato di missaggio del dispositivo) e
/// conversione al volo in PCM 16 kHz mono 16 bit (media dei canali, ricampionatore WDL). La memoria è
/// limitata a cinque minuti: oltre, l'audio in eccesso viene scartato e si registra un avviso nel log.
/// </summary>
public sealed class NAudioRecorder : IAudioRecorder
{
    private const int TargetSampleRate = 16000;
    private const int MaxSeconds = 300;
    private const int MaxOutputBytes = TargetSampleRate * 2 * MaxSeconds;
    private const int CaptureBufferMs = 100;

    private readonly ILog _log;
    private readonly object _lock = new();
    private readonly byte[] _pullBuffer = new byte[32 * 1024];

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _input;
    private IWaveProvider? _converter;
    private MemoryStream? _output;
    private bool _limitReported;
    private bool _disposed;

    public NAudioRecorder(ILog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsRecording
    {
        get
        {
            lock (_lock)
            {
                return _capture is not null;
            }
        }
    }

    /// <summary>Dispositivi di cattura attivi. IsDefault indica il dispositivo predefinito per le comunicazioni.</summary>
    public IReadOnlyList<AudioInputDevice> GetDevices()
    {
        var result = new List<AudioInputDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = GetDefaultDeviceId(enumerator);
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    result.Add(new AudioInputDevice(device.ID, device.FriendlyName, string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Microfono: impossibile elencare i dispositivi ({ex.GetType().Name}: {ex.Message}).");
        }

        return result;
    }

    /// <summary>
    /// Avvia la registrazione dal dispositivo indicato (Id WASAPI) o da quello predefinito se null, vuoto o
    /// non trovato. Se la registrazione è già in corso non fa nulla. Lancia InvalidOperationException se non
    /// esiste alcun microfono utilizzabile.
    /// </summary>
    public void Start(string? deviceId)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture is not null)
            {
                _log.Warn("Microfono: registrazione già in corso, avvio ignorato.");
                return;
            }
        }

        MMDevice? device = null;
        WasapiCapture? capture = null;
        try
        {
            device = ResolveDevice(deviceId);
            capture = new WasapiCapture(device, true, CaptureBufferMs);
            WaveFormat sourceFormat = capture.WaveFormat;

            var input = new BufferedWaveProvider(sourceFormat, TimeSpan.FromSeconds(2))
            {
                ReadFully = false,
                DiscardOnBufferOverflow = true,
            };
            ISampleProvider samples = input.ToSampleProvider();
            if (sourceFormat.Channels > 1)
            {
                samples = new MonoMixSampleProvider(samples);
            }

            if (sourceFormat.SampleRate != TargetSampleRate)
            {
                samples = new WdlResamplingSampleProvider(samples, TargetSampleRate);
            }

            var converter = new SampleToWaveProvider16(samples);

            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _device = device;
                _capture = capture;
                _input = input;
                _converter = converter;
                _output = new MemoryStream(TargetSampleRate * 2 * 30);
                _limitReported = false;
            }

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            _log.Info($"Microfono: registrazione avviata da '{device.FriendlyName}' ({sourceFormat.SampleRate} Hz, {sourceFormat.Channels} canali, {sourceFormat.Encoding}).");
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_capture, capture))
                {
                    ClearSessionState();
                }
            }

            if (capture is not null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                SafeDispose(capture);
            }

            SafeDispose(device);
            _log.Error("Microfono: impossibile avviare la registrazione.", ex);
            throw new InvalidOperationException("Nessun microfono utilizzabile.", ex);
        }
    }

    /// <summary>Ferma la registrazione e restituisce il PCM 16 kHz mono 16 bit raccolto (vuoto se non si stava registrando).</summary>
    public byte[] Stop()
    {
        WasapiCapture? capture;
        MMDevice? device;
        lock (_lock)
        {
            capture = _capture;
            device = _device;
        }

        if (capture is null)
        {
            return Array.Empty<byte>();
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception ex)
        {
            _log.Warn($"Microfono: errore nell'arresto ({ex.GetType().Name}).");
        }

        // Dispose attende il thread di cattura: gli ultimi pacchetti arrivano prima che ritorni.
        SafeDispose(capture);
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        SafeDispose(device);

        byte[] result;
        lock (_lock)
        {
            if (!ReferenceEquals(_capture, capture))
            {
                return Array.Empty<byte>();
            }

            Drain();
            result = _output?.ToArray() ?? Array.Empty<byte>();
            ClearSessionState();
        }

        _log.Info($"Microfono: registrazione terminata, {result.Length / (TargetSampleRate * 2.0):0.0} s di audio.");
        return result;
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

        try
        {
            Stop();
        }
        catch (Exception ex)
        {
            _log.Warn($"Microfono: errore in chiusura ({ex.GetType().Name}).");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            lock (_lock)
            {
                if (!ReferenceEquals(sender, _capture) || _input is null || e.BytesRecorded <= 0)
                {
                    return;
                }

                _input.AddSamples(e.Buffer, 0, e.BytesRecorded);
                Drain();
            }
        }
        catch (Exception ex)
        {
            // Thread di cattura di NAudio: nessuna eccezione deve uscire.
            _log.Error("Microfono: errore nella conversione dell'audio.", ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _log.Warn($"Microfono: la cattura si è fermata con errore ({e.Exception.GetType().Name}: {e.Exception.Message}).");
        }
    }

    /// <summary>Svuota il buffer d'ingresso attraverso la catena di conversione (da chiamare con il lock).</summary>
    private void Drain()
    {
        if (_converter is null || _output is null)
        {
            return;
        }

        while (true)
        {
            int read = _converter.Read(_pullBuffer, 0, _pullBuffer.Length);
            if (read <= 0)
            {
                break;
            }

            int room = MaxOutputBytes - (int)_output.Length;
            if (room <= 0)
            {
                if (!_limitReported)
                {
                    _limitReported = true;
                    _log.Warn($"Microfono: raggiunto il limite di {MaxSeconds} secondi, l'audio successivo viene scartato.");
                }

                continue;
            }

            _output.Write(_pullBuffer, 0, Math.Min(read, room));
        }
    }

    private void ClearSessionState()
    {
        _capture = null;
        _device = null;
        _input = null;
        _converter = null;
        _output?.Dispose();
        _output = null;
    }

    private MMDevice ResolveDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                MMDevice device = enumerator.GetDevice(deviceId.Trim());
                if (device.DataFlow == DataFlow.Capture && device.State == DeviceState.Active)
                {
                    return device;
                }

                _log.Warn($"Microfono: il dispositivo impostato non è attivo ({device.State}), uso il predefinito.");
                device.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn($"Microfono: dispositivo impostato non trovato ({ex.GetType().Name}), uso il predefinito.");
            }
        }

        foreach (Role role in new[] { Role.Communications, Role.Multimedia, Role.Console })
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            }
            catch (Exception)
            {
                // Nessun predefinito per questo ruolo: si prova il successivo.
            }
        }

        throw new InvalidOperationException("Nessun microfono predefinito disponibile.");
    }

    private static string? GetDefaultDeviceId(MMDeviceEnumerator enumerator)
    {
        foreach (Role role in new[] { Role.Communications, Role.Multimedia, Role.Console })
        {
            try
            {
                using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                return device.ID;
            }
            catch (Exception)
            {
                // Nessun predefinito per questo ruolo: si prova il successivo.
            }
        }

        return null;
    }

    private void SafeDispose(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"Microfono: errore nella chiusura di una risorsa ({ex.GetType().Name}).");
        }
    }
}
