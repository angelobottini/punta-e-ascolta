using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Audio;

/// <summary>
/// Brevi segnali acustici generati in PCM (16 bit mono), a volume moderato, con rampe di attacco e
/// rilascio per evitare scatti. Ogni chiamata genera un nuovo array: il chiamante può tenerlo o scartarlo.
/// </summary>
public static class SystemSounds
{
    /// <summary>Formato di tutti i segnali: 22,05 kHz, mono, 16 bit.</summary>
    public static readonly PcmFormat Format = new(22050, 1, 16);

    private const double Amplitude = 0.25;
    private const int RampMs = 6;

    /// <summary>Inizio dettatura: due note ascendenti.</summary>
    public static (byte[] Pcm, PcmFormat Format) DictationStart() =>
        (Render((660, 90), (0, 25), (880, 130)), Format);

    /// <summary>Fine dettatura: due note discendenti.</summary>
    public static (byte[] Pcm, PcmFormat Format) DictationStop() =>
        (Render((880, 90), (0, 25), (660, 130)), Format);

    /// <summary>Errore: nota bassa ripetuta due volte.</summary>
    public static (byte[] Pcm, PcmFormat Format) Error() =>
        (Render((220, 140), (0, 60), (220, 140)), Format);

    /// <summary>Conferma: una nota breve.</summary>
    public static (byte[] Pcm, PcmFormat Format) Ack() =>
        (Render((1000, 70)), Format);

    /// <summary>Concatena segmenti (frequenza in Hz, durata in ms); frequenza 0 = pausa.</summary>
    private static byte[] Render(params (int FrequencyHz, int DurationMs)[] segments)
    {
        int totalSamples = 0;
        foreach (var (_, durationMs) in segments)
        {
            totalSamples += Format.SampleRate * durationMs / 1000;
        }

        byte[] pcm = new byte[totalSamples * 2];
        int index = 0;
        foreach (var (frequencyHz, durationMs) in segments)
        {
            int samples = Format.SampleRate * durationMs / 1000;
            if (frequencyHz <= 0)
            {
                index += samples * 2;
                continue;
            }

            int ramp = Math.Min(Format.SampleRate * RampMs / 1000, samples / 2);
            double step = 2.0 * Math.PI * frequencyHz / Format.SampleRate;
            for (int i = 0; i < samples; i++)
            {
                double envelope = 1.0;
                if (i < ramp)
                {
                    envelope = (double)i / ramp;
                }
                else if (i >= samples - ramp)
                {
                    envelope = (double)(samples - 1 - i) / ramp;
                }

                short value = (short)Math.Round(Math.Sin(step * i) * Amplitude * envelope * short.MaxValue);
                pcm[index++] = (byte)(value & 0xFF);
                pcm[index++] = (byte)((value >> 8) & 0xFF);
            }
        }

        return pcm;
    }
}
