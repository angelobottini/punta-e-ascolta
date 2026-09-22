using NAudio.Wave;

namespace PuntaEAscolta.Windows.Audio;

/// <summary>Riduce un flusso a N canali in mono facendo la media dei canali (funziona anche con letture parziali).</summary>
internal sealed class MonoMixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _sourceBuffer = Array.Empty<float>();
    private int _carry;

    public MonoMixSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = source.WaveFormat.Channels;
        if (_channels < 1)
        {
            throw new ArgumentException("Il flusso sorgente non ha canali.", nameof(source));
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        int needed = count * _channels;
        if (_sourceBuffer.Length < needed)
        {
            Array.Resize(ref _sourceBuffer, needed);
        }

        // I campioni spaiati della lettura precedente restano in testa al buffer.
        int read = _source.Read(_sourceBuffer, _carry, needed - _carry) + _carry;
        int frames = read / _channels;
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0f;
            int baseIndex = frame * _channels;
            for (int channel = 0; channel < _channels; channel++)
            {
                sum += _sourceBuffer[baseIndex + channel];
            }

            buffer[offset + frame] = sum / _channels;
        }

        _carry = read - frames * _channels;
        if (_carry > 0)
        {
            Array.Copy(_sourceBuffer, frames * _channels, _sourceBuffer, 0, _carry);
        }

        return frames;
    }
}
