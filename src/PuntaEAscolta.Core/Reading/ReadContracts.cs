using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Core.Reading;

/// <summary>Che cosa è stato chiesto di leggere.</summary>
public enum ReadRequestKind
{
    /// <summary>L'elemento, la frase o la riga sotto il puntatore (clic breve).</summary>
    AtPointer,
    /// <summary>Tutto il testo della zona attorno al puntatore (pressione prolungata, se abilitata).</summary>
    ZoneAroundPointer,
    /// <summary>Il testo selezionato nell'app in primo piano.</summary>
    Selection
}

public sealed record ReadRequest(ReadRequestKind Kind, ScreenPoint Point);

/// <summary>Da dove proviene il testo letto (utile per diagnosi e test automatici).</summary>
public enum ReadSource
{
    None, Selection, ClipboardSelection, UiaName, UiaValue, UiaDescription, UiaSentence, Tooltip, TooltipOcr,
    OcrLine, OcrBlock, OcrZone
}

/// <summary>Esito di una risoluzione del testo. Text è già ripulito (niente emoji, niente scorciatoie) ed è ciò che verrà pronunciato.</summary>
public sealed record ReadOutcome(
    ReadSource Source,
    string Text,
    SpeechKind SpeechKind,
    string? LanguageHint,
    bool Sensitive,
    int ElapsedMs,
    string Diagnostics)
{
    public bool HasText => Source != ReadSource.None && !string.IsNullOrWhiteSpace(Text);
    public static ReadOutcome Nothing(int elapsedMs, string diagnostics) =>
        new(ReadSource.None, "", SpeechKind.System, null, false, elapsedMs, diagnostics);
}

/// <summary>Risolve che cosa leggere per una richiesta, SENZA parlare. Usato dall'orchestratore e dalla modalità di prova a riga di comando.</summary>
public interface ITextResolver
{
    Task<ReadOutcome> ResolveAsync(ReadRequest request, CancellationToken ct);
}

/// <summary>
/// Cuore dell'app: riceve gli eventi di input, applica la logica "secondo clic = stop", risolve il testo e lo fa pronunciare.
/// HandleInput non blocca mai (accoda); l'elaborazione avviene su un worker interno, una richiesta alla volta,
/// e una richiesta nuova annulla quella in corso.
/// </summary>
public interface IReadOrchestrator : IDisposable
{
    void HandleInput(InputEvent inputEvent);
    event Action<ReadOutcome>? OutcomeProduced;
    bool Paused { get; set; }
}
