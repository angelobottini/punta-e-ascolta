using System.Buffers.Binary;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Audio;

/// <summary>Lettura minimale di un'intestazione RIFF/WAVE: formato reale e posizione del blocco "data".</summary>
internal static class WavHeader
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>
    /// Analizza l'intestazione. Restituisce false se il contenuto non è un WAV PCM riconoscibile.
    /// <paramref name="isFloat"/> è vero per campioni IEEE float a 32 bit (PcmFormat non lo distingue: vanno convertiti).
    /// <paramref name="dataOffset"/> e <paramref name="dataLength"/> delimitano i campioni grezzi (solo frame interi).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> wav, out PcmFormat format, out bool isFloat, out int dataOffset, out int dataLength)
    {
        format = new PcmFormat(0);
        isFloat = false;
        dataOffset = 0;
        dataLength = 0;

        if (wav.Length < 12 || !Tag(wav, 0, "RIFF") || !Tag(wav, 8, "WAVE"))
        {
            return false;
        }

        bool haveFormat = false;
        int position = 12;
        while (position + 8 <= wav.Length)
        {
            int chunkSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(position + 4, 4)), int.MaxValue);
            int body = position + 8;

            if (Tag(wav, position, "fmt "))
            {
                if (chunkSize < 16 || body + 16 > wav.Length)
                {
                    return false;
                }

                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body, 2));
                int channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 2, 2));
                int sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(body + 4, 4));
                int bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 14, 2));

                if (tag == FormatExtensible && chunkSize >= 40 && body + 26 <= wav.Length)
                {
                    // Nel formato estensibile il vero tipo è nei primi due byte del sottoformato (GUID).
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 24, 2));
                }

                if (tag != FormatPcm && tag != FormatIeeeFloat)
                {
                    return false;
                }

                if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0)
                {
                    return false;
                }

                if (tag == FormatIeeeFloat && bitsPerSample != 32)
                {
                    return false;
                }

                format = new PcmFormat(sampleRate, channels, bitsPerSample);
                isFloat = tag == FormatIeeeFloat;
                haveFormat = true;
            }
            else if (Tag(wav, position, "data"))
            {
                if (!haveFormat)
                {
                    return false;
                }

                dataOffset = body;
                // Alcuni produttori scrivono dimensioni provvisorie (0 o 0xFFFFFFFF): ci si affida ai byte presenti.
                dataLength = Math.Min(chunkSize, wav.Length - body);
                if (dataLength < 0)
                {
                    dataLength = 0;
                }

                // Solo frame interi: un eventuale byte finale spaiato si scarta.
                int blockAlign = Math.Max(1, format.Channels * format.BitsPerSample / 8);
                dataLength -= dataLength % blockAlign;

                return true;
            }

            // I blocchi RIFF sono allineati a 2 byte.
            long next = (long)body + chunkSize + (chunkSize & 1);
            if (next > int.MaxValue)
            {
                return false;
            }

            position = (int)next;
        }

        return false;
    }

    private static bool Tag(ReadOnlySpan<byte> data, int offset, string tag) =>
        data.Length >= offset + 4
        && data[offset] == tag[0]
        && data[offset + 1] == tag[1]
        && data[offset + 2] == tag[2]
        && data[offset + 3] == tag[3];
}
