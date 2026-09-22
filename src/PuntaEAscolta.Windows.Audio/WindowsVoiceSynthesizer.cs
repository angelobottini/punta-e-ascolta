using System.Globalization;
using System.Text;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using Windows.Media.SpeechSynthesis;

namespace PuntaEAscolta.Windows.Audio;

/// <summary>
/// Voce locale di Windows (OneCore) tramite Windows.Media.SpeechSynthesis. La sintesi non è in streaming:
/// il WAV completo arriva in memoria, se ne legge l'intestazione e si espone il solo blocco dati come PCM.
/// La voce si sceglie da Speech.WindowsVoiceName (Id o nome visualizzato, anche parziale); in mancanza la
/// prima voce it-IT, poi quella predefinita di Windows. Velocità da Speech.WindowsRate tramite SSML.
/// </summary>
public sealed class WindowsVoiceSynthesizer : ISpeechSynthesizer, IDisposable
{
    private const string ItalianLanguage = "it-IT";
    private const string EnglishLanguage = "en-US";
    private const double MinRate = 0.5;
    private const double MaxRate = 3.0;

    private readonly ISettingsStore _settings;
    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SpeechSynthesizer? _synthesizer;
    private bool _disposed;

    public WindowsVoiceSynthesizer(ISettingsStore settings, ILog log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Id => "windows";

    /// <summary>Vero se è installata almeno una voce.</summary>
    public bool IsConfigured => GetVoices().Count > 0;

    /// <summary>Voci OneCore installate, in ordine di lingua e nome. Vuoto se l'API non risponde.</summary>
    public static IReadOnlyList<WindowsVoiceInfo> GetVoices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices
                .Select(v => new WindowsVoiceInfo(v.Id, v.DisplayName, v.Language, v.Gender.ToString()))
                .OrderBy(v => v.Language, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<WindowsVoiceInfo>();
        }
    }

    public async Task<SpeechAudio> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string text = ResolveText(request);
        if (string.IsNullOrWhiteSpace(text))
        {
            // Niente da dire: stream vuoto, il lettore termina subito.
            return new SpeechAudio(new MemoryStream(Array.Empty<byte>(), writable: false), new PcmFormat(16000));
        }

        SpeechSettings speech = _settings.Current.Speech;
        VoiceInformation? voice = SelectVoice(speech.WindowsVoiceName, request.LanguageHint)
            ?? throw new InvalidOperationException("Nessuna voce di Windows installata.");
        string language = ResolveLanguage(voice);
        double rate = Math.Clamp(double.IsFinite(speech.WindowsRate) && speech.WindowsRate > 0 ? speech.WindowsRate : 1.0, MinRate, MaxRate);

        if (_log.IsDebugEnabled)
        {
            _log.Debug($"Voce di Windows: '{voice.DisplayName}' ({language}), velocità {rate.ToString("0.##", CultureInfo.InvariantCulture)}, testo: {text}");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SpeechSynthesizer synthesizer = GetSynthesizer();
            synthesizer.Voice = voice;

            byte[] wav;
            try
            {
                string ssml = BuildSsml(text, language, rate);
                wav = await SynthesizeToBytesAsync(synthesizer.SynthesizeSsmlToStreamAsync(ssml), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Alcuni testi o voci rifiutano l'SSML: si ritenta con il testo semplice e la velocità dalle opzioni.
                _log.Warn($"Voce di Windows: SSML rifiutato ({ex.GetType().Name}), ritento con testo semplice.");
                TrySetSpeakingRate(synthesizer, rate);
                wav = await SynthesizeToBytesAsync(synthesizer.SynthesizeTextToStreamAsync(text), ct).ConfigureAwait(false);
            }

            if (!WavHeader.TryParse(wav, out PcmFormat format, out int dataOffset, out int dataLength))
            {
                throw new InvalidOperationException("Voce di Windows: il flusso restituito non è un WAV PCM riconoscibile.");
            }

            if (format.BitsPerSample != 16)
            {
                _log.Warn($"Voce di Windows: formato inatteso {format.SampleRate} Hz, {format.Channels} canali, {format.BitsPerSample} bit.");
            }

            return new SpeechAudio(new MemoryStream(wav, dataOffset, dataLength, writable: false), format);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Paga in anticipo il costo della prima sintesi (caricamento del motore e della voce, misurato in
    /// circa 0,5 s a freddo) sintetizzando una parola e scartandola. Facoltativo, non lancia eccezioni.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using SpeechAudio audio = await SynthesizeAsync(new SpeechRequest("Pronto", SpeechKind.System, "it"), ct).ConfigureAwait(false);
            _log.Info($"Voce di Windows: preriscaldamento in {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (OperationCanceledException)
        {
            // Avvio interrotto: nulla da fare.
        }
        catch (Exception ex)
        {
            _log.Warn($"Voce di Windows: preriscaldamento non riuscito ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // Il semaforo non si elimina: una sintesi in corso deve poterlo rilasciare senza eccezioni.
            _synthesizer?.Dispose();
            _synthesizer = null;
        }
        catch (Exception ex)
        {
            _log.Warn($"Voce di Windows: errore in chiusura ({ex.GetType().Name}).");
        }
    }

    private SpeechSynthesizer GetSynthesizer()
    {
        if (_synthesizer is null)
        {
            var synthesizer = new SpeechSynthesizer();
            try
            {
                synthesizer.Options.IncludeWordBoundaryMetadata = false;
                synthesizer.Options.IncludeSentenceBoundaryMetadata = false;
                // Taglia il silenzio in coda: le etichette brevi terminano prima.
                synthesizer.Options.AppendedSilence = SpeechAppendedSilence.Min;
            }
            catch (Exception ex)
            {
                _log.Warn($"Voce di Windows: opzioni non applicabili ({ex.GetType().Name}).");
            }

            _synthesizer = synthesizer;
        }

        return _synthesizer;
    }

    private static async Task<byte[]> SynthesizeToBytesAsync(global::Windows.Foundation.IAsyncOperation<SpeechSynthesisStream> operation, CancellationToken ct)
    {
        using SpeechSynthesisStream stream = await operation.AsTask(ct).ConfigureAwait(false);
        int capacity = (int)Math.Min(stream.Size, int.MaxValue);
        using Stream source = stream.AsStreamForRead();
        using var memory = new MemoryStream(capacity);
        await source.CopyToAsync(memory, ct).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static string ResolveText(SpeechRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Text))
        {
            return request.Text;
        }

        return request.Chunks is { Count: > 0 } ? string.Join(" ", request.Chunks) : string.Empty;
    }

    /// <summary>
    /// Voce da usare: quella impostata (Id, nome esatto o parte del nome), altrimenti la prima it-IT, poi la
    /// predefinita. Con indizio di lingua "en" e una voce inglese installata si usa quella.
    /// </summary>
    private static VoiceInformation? SelectVoice(string configuredName, string? languageHint)
    {
        IReadOnlyList<VoiceInformation> all;
        try
        {
            all = SpeechSynthesizer.AllVoices;
        }
        catch (Exception)
        {
            return null;
        }

        if (all.Count == 0)
        {
            return null;
        }

        if (string.Equals(languageHint, "en", StringComparison.OrdinalIgnoreCase))
        {
            VoiceInformation? english = all.FirstOrDefault(v => string.Equals(v.Language, EnglishLanguage, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (english is not null)
            {
                return english;
            }
        }

        string wanted = configuredName?.Trim() ?? string.Empty;
        if (wanted.Length > 0)
        {
            VoiceInformation? match = all.FirstOrDefault(v => string.Equals(v.Id, wanted, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(v => string.Equals(v.DisplayName, wanted, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(v => v.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return all.FirstOrDefault(v => string.Equals(v.Language, ItalianLanguage, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(v => v.Language.StartsWith("it", StringComparison.OrdinalIgnoreCase))
            ?? SafeDefaultVoice()
            ?? all[0];
    }

    private static VoiceInformation? SafeDefaultVoice()
    {
        try
        {
            return SpeechSynthesizer.DefaultVoice;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ResolveLanguage(VoiceInformation voice)
    {
        if (voice.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(voice.Language) ? EnglishLanguage : voice.Language;
        }

        return voice.Language.StartsWith("it", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(voice.Language)
            ? ItalianLanguage
            : voice.Language;
    }

    private static void TrySetSpeakingRate(SpeechSynthesizer synthesizer, double rate)
    {
        try
        {
            synthesizer.Options.SpeakingRate = rate;
        }
        catch (Exception)
        {
            // Opzione non disponibile: si parla a velocità normale.
        }
    }

    /// <summary>SSML 1.0 con lingua e velocità; il testo viene ripulito dai caratteri non ammessi in XML e poi escapato.</summary>
    internal static string BuildSsml(string text, string language, double rate)
    {
        var sb = new StringBuilder(text.Length + 160);
        sb.Append("<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"")
          .Append(language)
          .Append("\">");

        bool withProsody = Math.Abs(rate - 1.0) > 0.001;
        if (withProsody)
        {
            sb.Append("<prosody rate=\"").Append(rate.ToString("0.##", CultureInfo.InvariantCulture)).Append("\">");
        }

        sb.Append(EscapeXml(text));

        if (withProsody)
        {
            sb.Append("</prosody>");
        }

        sb.Append("</speak>");
        return sb.ToString();
    }

    internal static string EscapeXml(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c).Append(text[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c) || IsInvalidXmlChar(c))
                    {
                        sb.Append(' ');
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Caratteri di controllo non ammessi in XML 1.0 (restano tab, a capo e ritorno carrello).</summary>
    private static bool IsInvalidXmlChar(char c) =>
        (c < 0x20 && c != 0x09 && c != 0x0A && c != 0x0D) || c == 0xFFFE || c == 0xFFFF;
}
