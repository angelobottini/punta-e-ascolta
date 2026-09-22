using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PuntaEAscolta.Windows.Audio;

/// <summary>
/// Converte al volo l'audio catturato (formato di missaggio del dispositivo: di solito float 48 kHz stereo)
/// in PCM 16 kHz mono 16 bit: media dei canali, ricampionatore WDL, conversione a 16 bit. L'uscita è limitata
/// a <see cref="MaxOutputBytes"/>: oltre, l'audio viene scartato. Non è thread-safe: la protegge il chiamante.
/// </summary>
internal sealed class Pcm16kMonoConverter
{
    public const int TargetSampleRate = 16000;

    private readonly BufferedWaveProvider _input;
    private readonly IWaveProvider _converter;
    private readonly MemoryStream _output;
    private readonly byte[] _pullBuffer = new byte[32 * 1024];

    public Pcm16kMonoConverter(WaveFormat sourceFormat, int maxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);
        MaxOutputBytes = maxOutputBytes;

        _input = new BufferedWaveProvider(sourceFormat, TimeSpan.FromSeconds(2))
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
        };

        ISampleProvider samples = _input.ToSampleProvider();
        if (sourceFormat.Channels > 1)
        {
            samples = new MonoMixSampleProvider(samples);
        }

        if (sourceFormat.SampleRate != TargetSampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, TargetSampleRate);
        }

        _converter = new SampleToWaveProvider16(samples);
        _output = new MemoryStream(Math.Min(maxOutputBytes, TargetSampleRate * 2 * 30));
    }

    public int MaxOutputBytes { get; }

    /// <summary>Vero se si è raggiunto il limite e dell'audio è stato scartato.</summary>
    public bool LimitReached { get; private set; }

    public int OutputLength => (int)_output.Length;

    /// <summary>Aggiunge un pacchetto nel formato sorgente e lo converte subito.</summary>
    public void Write(byte[] buffer, int count)
    {
        if (count <= 0)
        {
            return;
        }

        // Pacchetti più grandi del buffer d'ingresso (2 s) si spezzano per non scartare nulla.
        int offset = 0;
        int blockAlign = Math.Max(1, _input.WaveFormat.BlockAlign);
        int slice = Math.Max(blockAlign, _input.BufferLength / 2 / blockAlign * blockAlign);
        while (offset < count)
        {
            int length = Math.Min(slice, count - offset);
            _input.AddSamples(buffer, offset, length);
            offset += length;
            Drain();
        }
    }

    /// <summary>PCM 16 kHz mono 16 bit raccolto finora.</summary>
    public byte[] ToArray()
    {
        Drain();
        return _output.ToArray();
    }

    private void Drain()
    {
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
                LimitReached = true;
                continue;
            }

            int accepted = Math.Min(read, room);
            if (accepted < read)
            {
                LimitReached = true;
            }

            _output.Write(_pullBuffer, 0, accepted);
        }
    }
}
