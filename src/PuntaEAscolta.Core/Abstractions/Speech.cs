namespace PuntaEAscolta.Core.Abstractions;

/// <summary>Label = etichetta breve (voce di menu, pulsante); Sentence = frase o blocco; System = messaggi dell'app ("Nessun testo"), sempre con la voce locale.</summary>
public enum SpeechKind { Label, Sentence, System }

/// <param name="LanguageHint">"it", "en" oppure null se sconosciuta.</param>
/// <param name="Sensitive">True per testo di documenti: con l'opzione di riservatezza attiva va letto solo con la voce locale.</param>
public sealed record SpeechRequest(string Text, SpeechKind Kind, string? LanguageHint = null, bool Sensitive = false)
{
    /// <summary>
    /// Testo già spezzato in frasi da chi chiama (la logica portabile). Se presente, il servizio vocale pronuncia i pezzi
    /// in sequenza, preparando il successivo mentre parla: così uno Stop non spreca crediti sul testo non ancora letto.
    /// Se null, Text è un pezzo unico.
    /// </summary>
    public IReadOnlyList<string>? Chunks { get; init; }
}

public sealed record PcmFormat(int SampleRate, int Channels = 1, int BitsPerSample = 16)
{
    public int BytesPerSecond => SampleRate * Channels * BitsPerSample / 8;
}

/// <summary>Audio PCM grezzo (little endian, senza intestazione WAV). Lo Stream può essere di rete: si legge in sequenza fino alla fine.</summary>
public sealed class SpeechAudio(Stream pcm, PcmFormat format) : IAsyncDisposable
{
    public Stream Pcm { get; } = pcm;
    public PcmFormat Format { get; } = format;
    public ValueTask DisposeAsync() => Pcm.DisposeAsync();
}

/// <summary>Sintetizzatore vocale (ElevenLabs, voce di Windows...). Deve restituire il prima possibile: lo stream può essere ancora in arrivo.</summary>
public interface ISpeechSynthesizer
{
    string Id { get; }
    bool IsConfigured { get; }
    Task<SpeechAudio> SynthesizeAsync(SpeechRequest request, CancellationToken ct);
}

/// <summary>Riproduzione audio con arresto immediato. PlayAsync termina quando l'audio è finito o è stato fermato o annullato.</summary>
public interface IAudioPlayer : IDisposable
{
    bool IsPlaying { get; }
    Task PlayAsync(Stream pcm, PcmFormat format, double volume, CancellationToken ct);
    void Stop();
}

/// <summary>Servizio vocale di alto livello: scelta del fornitore, cache, spezzatura in frasi, ripiego sulla voce locale.</summary>
public interface ISpeechService : IDisposable
{
    bool IsSpeaking { get; }
    event Action<bool>? SpeakingChanged;

    /// <summary>Interrompe l'eventuale lettura in corso e legge la richiesta. Termina a fine lettura o allo Stop.</summary>
    Task SpeakAsync(SpeechRequest request, CancellationToken ct);
    void Stop();
}

/// <summary>Protezione dei segreti a riposo (Windows: DPAPI utente corrente). Il testo protetto è Base64.</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    /// <summary>Restituisce null se il dato non è decifrabile (es. cartella copiata su un altro PC o altro utente).</summary>
    string? Unprotect(string protectedText);
}
