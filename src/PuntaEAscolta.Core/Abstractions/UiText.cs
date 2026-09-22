namespace PuntaEAscolta.Core.Abstractions;

/// <summary>Tipo di elemento, astratto rispetto alla piattaforma (mappato dai ControlType di UI Automation).</summary>
public enum UiElementKind
{
    Unknown, Button, SplitButton, MenuBar, Menu, MenuItem, CheckBox, RadioButton, ComboBox, Edit, Document, Text,
    Hyperlink, Image, List, ListItem, Tree, TreeItem, Tab, TabItem, DataGrid, DataItem, Header, HeaderItem,
    Slider, Spinner, ProgressBar, ScrollBar, StatusBar, ToolBar, ToolTip, TitleBar, Group, Pane, Window, Custom
}

public enum UiToggleState { None, Off, On, Indeterminate }

/// <summary>Contesto testuale quando l'elemento espone un contenuto di testo navigabile (UIA TextPattern).</summary>
/// <param name="ParagraphText">Testo del paragrafo che contiene il punto, così come lo restituisce la piattaforma.</param>
/// <param name="OffsetInParagraph">Indice del carattere puntato dentro ParagraphText.</param>
/// <param name="PointerOverText">True se il punto cade davvero sopra del testo (e non su margini o area vuota).</param>
public sealed record UiTextContext(string ParagraphText, int OffsetInParagraph, bool PointerOverText);

/// <summary>
/// Fotografia dell'elemento sotto il puntatore. Lo strato di piattaforma si limita a RACCOGLIERE i dati;
/// la decisione su che cosa leggere spetta alla logica portabile (UiTextResolver).
/// Tutte le stringhe possono essere null o vuote. ParentName e ParentKind descrivono il genitore diretto.
/// </summary>
public sealed record UiElementInfo
{
    public UiElementKind Kind { get; init; }
    public string? Name { get; init; }
    public string? Value { get; init; }
    public string? HelpText { get; init; }
    public string? FullDescription { get; init; }
    public string? LegacyName { get; init; }
    public string? LegacyDescription { get; init; }
    public string? LegacyValue { get; init; }
    public string? ItemStatus { get; init; }
    public string? AcceleratorKey { get; init; }
    public string? AccessKey { get; init; }
    public string? LabeledByName { get; init; }
    public UiToggleState ToggleState { get; init; }
    public bool IsPassword { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public string? AutomationId { get; init; }
    public string? ProcessName { get; init; }
    public ScreenRect Bounds { get; init; }
    public UiElementKind ParentKind { get; init; }
    public string? ParentName { get; init; }
    public UiTextContext? Text { get; init; }

    /// <summary>Millisecondi impiegati dalla piattaforma a raccogliere i dati (diagnostica).</summary>
    public int ElapsedMs { get; init; }
}

public sealed record UiSelectionInfo(string Text, IReadOnlyList<ScreenRect> Bounds);

/// <summary>Suggerimento (tooltip) visibile vicino al puntatore. Text può essere null se la finestra non espone testo: in tal caso si fa l'OCR di Bounds.</summary>
public sealed record UiTooltipInfo(string? Text, ScreenRect Bounds);

/// <summary>
/// Accesso al testo dell'interfaccia tramite le API di accessibilità della piattaforma (Windows: UI Automation COM).
/// Tutti i metodi devono: non bloccare mai oltre il timeout interno (restituire null), non lanciare eccezioni
/// per elementi spariti o app che non rispondono, non spostare MAI il focus.
/// </summary>
public interface IUiTextSource : IDisposable
{
    Task<UiElementInfo?> GetElementAtAsync(ScreenPoint point, CancellationToken ct);

    /// <summary>Selezione di testo corrente nell'elemento con il focus. null se non c'è selezione o non è leggibile.</summary>
    Task<UiSelectionInfo?> GetSelectionAsync(CancellationToken ct);

    /// <summary>Cerca una finestra di suggerimento visibile vicino al punto (entro circa 400 px fisici).</summary>
    Task<UiTooltipInfo?> FindTooltipAsync(ScreenPoint point, CancellationToken ct);
}

/// <summary>Ripiego per "leggi la selezione" quando l'accessibilità non la espone: copia negli appunti e ripristina.</summary>
public interface IClipboardSelectionReader
{
    /// <summary>
    /// Invia Ctrl+C all'app in primo piano e restituisce il testo copiato, ripristinando gli appunti precedenti.
    /// DEVE rinunciare (restituire null) se gli appunti contengono dati non testuali, ad esempio un'immagine copiata in Photoshop o Affinity.
    /// </summary>
    Task<string?> TryCopySelectionAsync(CancellationToken ct);
}
