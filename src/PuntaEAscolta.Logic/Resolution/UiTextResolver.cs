using System.Text.RegularExpressions;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Text;

namespace PuntaEAscolta.Logic.Resolution;

/// <summary>Esito della decisione su un elemento dell'interfaccia. Text è già ripulito e pronto per la voce.</summary>
public sealed record UiResolution(ReadSource Source, string Text, SpeechKind Kind, bool Sensitive);

/// <summary>
/// Decide che cosa pronunciare a partire dai dati raccolti dall'accessibilità. null = passare a tooltip e OCR.
/// Vedi docs/DESIGN.md sez. 3.1, docs/research/probe-office.md (priorità: Name; FullDescription; Excel Value; "Altre opzioni")
/// e docs/research/probe-affinity.md (nomi che sono tipi .NET, "StudioPage, Title = X", separatori con nome di tipo).
/// </summary>
public static partial class UiTextResolver
{
    /// <summary>Nome di tipo .NET: almeno due segmenti separati da punto, senza spazi ("Serif.Affinity.Workspaces.Workspace", "System.Windows.Controls.Grid").</summary>
    [GeneratedRegex(@"^(?:[A-Z][A-Za-z0-9_]*\.){2,}[A-Z][A-Za-z0-9_]*(?:\[\])?$|^[A-Z][A-Za-z0-9_]*\.[A-Z][A-Za-z0-9_]*(?:ViewModel|View|Model|Control|Separator|Page|Item|Panel|Pane|Host|Wrapper|Presenter|Element|Adorner)$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeName();

    /// <summary>ToString di oggetti WPF/.NET: "StudioPage, Title = Colore", "Foo { Bar = 1 }", "PaneClassDC".</summary>
    [GeneratedRegex(@"^\s*[A-Za-z_]\w*\s*,\s*(?:Title|Name|Header|Text|Label|Caption)\s*=\s*(?<v>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ToStringWithTitle();

    [GeneratedRegex(@"^\s*[A-Za-z_]\w*\s*\{.*\}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ToStringBraces();

    /// <summary>Identificatore in camelCase/PascalCase o snake_case senza spazi, tipico di AutomationId finito nel Name ("btnSaveAs", "PaneClassDC", "menu_item_3").</summary>
    [GeneratedRegex(@"^(?:[a-z]+(?:[A-Z][a-z0-9]*){1,}|[A-Za-z]+(?:_[A-Za-z0-9]+)+|[A-Z][a-z]+(?:[A-Z][a-zA-Z0-9]*){2,}|\w+(?:Class|Ctrl|Control|View|Model|ViewModel|Host|Wrapper|Panel|Pane|DC|Hwnd)\w*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    /// <summary>Identificatore tutto minuscolo e attaccato ("newdocnew", "newdocopen" di Affinity): con un HelpText si preferisce quello.</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9]{6,}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowercaseIdentifier();

    private const int ContainerMinWidth = 300;
    private const int ContainerMinHeight = 120;
    private const int MaxContainerNameLength = 120;

    /// <summary>Valore di un campo pronunciato insieme all'etichetta quando il puntatore non è sul testo: oltre, o su più righe, è un documento.</summary>
    private const int MaxInlineValueChars = 200;

    /// <summary>Una cella che non è di un foglio di calcolo (tabella di Word) con testo lungo si legge per frasi, non per intero.</summary>
    private const int MaxCellValueCharsBeforeSentence = 120;

    /// <summary>Altezza massima in pixel di un separatore di menu travestito da MenuItem (Affinity: 6 px al 125%).</summary>
    private const int SeparatorMaxHeight = 12;

    public static UiResolution? Resolve(UiElementInfo info, ReadingSettings settings)
    {
        if (info is null) return null;

        // 1. Contenuto di testo puntato (Word, editor, campi lunghi): la frase sotto il punto.
        // Le celle di un foglio di calcolo possono avere anche un contesto di testo: lì conta il valore visualizzato
        // (Excel: Name = coordinata "C3"). Una cella di tabella di Word con un paragrafo lungo si legge invece per frasi.
        bool cellWithValue = info.Kind is UiElementKind.DataItem && !string.IsNullOrWhiteSpace(info.Value)
            && (LooksLikeCellReference(info.Name) || !IsLongOrMultiline(info.Value, MaxCellValueCharsBeforeSentence));
        if (!cellWithValue && info.Text is { PointerOverText: true } ctx && settings.ReadSentenceInDocuments && !info.IsPassword)
        {
            var sentence = SentenceSplitter.ExtractSentence(ctx.ParagraphText, ctx.OffsetInParagraph);
            sentence = Finish(sentence, settings);
            if (sentence.Length > 0) return new UiResolution(ReadSource.UiaSentence, sentence, SpeechKind.Sentence, Sensitive: true);
        }

        // 2. Cella di foglio di calcolo o voce di griglia: il valore visualizzato (il Name è solo la coordinata).
        if (info.Kind is UiElementKind.DataItem or UiElementKind.HeaderItem)
        {
            var value = FirstUsable(info.Value, info.LegacyValue);
            if (value is not null) return new UiResolution(ReadSource.UiaValue, Finish(value, settings), SpeechKind.Sentence, Sensitive: true);
            if (info.Kind == UiElementKind.DataItem)
            {
                // Cella vuota: se l'elemento è una cella (nome tipo "C3") si annuncia "Cella vuota"
                if (LooksLikeCellReference(info.Name) && !string.IsNullOrWhiteSpace(settings.EmptyCellText))
                    return new UiResolution(ReadSource.UiaValue, settings.EmptyCellText, SpeechKind.Label, Sensitive: false);
                var name = UsableName(info);
                return name is null ? null : new UiResolution(ReadSource.UiaName, Finish(name, settings), SpeechKind.Label, Sensitive: false);
            }
        }

        // 3. Campi di testo e caselle combinate: etichetta più valore (mai il valore delle password).
        if (info.Kind is UiElementKind.Edit or UiElementKind.ComboBox or UiElementKind.Spinner)
        {
            var label = UsableName(info) ?? FirstUsable(info.LabeledByName);
            if (info.IsPassword)
                return label is null ? null : new UiResolution(ReadSource.UiaName, Finish(label, settings), SpeechKind.Label, false);
            var value = FirstUsable(info.Value, info.LegacyValue);
            if (info.Text is { } t && !t.PointerOverText && value is null && t.ParagraphText.Length > 0) value = t.ParagraphText;

            // Area di testo grande (pagina di Word "Contenuto pagina 1", Blocco note, corpo di un messaggio) con il puntatore
            // fuori dal testo: né il nome della pagina né il documento intero; si passa a suggerimento e OCR.
            bool largeArea = info.Bounds.Width >= ContainerMinWidth && info.Bounds.Height >= ContainerMinHeight;
            bool pointerOffText = info.Text is { PointerOverText: false } || info.Text is null;
            if (largeArea && pointerOffText && (info.Text is not null || (value is not null && IsLongOrMultiline(value, MaxInlineValueChars))))
                return null;
            // Valore su più righe o molto lungo (un documento, non un campo): si dice solo l'etichetta, mai il testo intero.
            if (value is not null && IsLongOrMultiline(value, MaxInlineValueChars)) value = null;

            // Esplora file: la scritta sotto l'icona è un Edit "Nome" con valore "lib.ps1" dentro la voce "lib.ps1": basta il nome del file.
            if (value is not null && info.ParentKind is UiElementKind.ListItem or UiElementKind.TreeItem or UiElementKind.DataItem
                && string.Equals(value, info.ParentName?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new UiResolution(ReadSource.UiaValue, Finish(value, settings), SpeechKind.Label, Sensitive: false);
            }

            if (label is null && value is null) return null;
            if (value is null) return new UiResolution(ReadSource.UiaName, Finish(label!, settings), SpeechKind.Label, false);
            var text = label is null ? value : $"{label}, {value}";
            return new UiResolution(ReadSource.UiaValue, Finish(text, settings), value.Length > 60 ? SpeechKind.Sentence : SpeechKind.Label, Sensitive: true);
        }

        // 4. Contenitori grandi o senza nome utile: silenzio, si passa a tooltip e OCR.
        if (IsContainer(info.Kind))
        {
            var name = UsableName(info);
            bool large = info.Bounds.Width >= ContainerMinWidth && info.Bounds.Height >= ContainerMinHeight;
            if (name is null || large || name.Length > MaxContainerNameLength) return null;
            if (info.Kind is UiElementKind.Window or UiElementKind.TitleBar) return null;
            return new UiResolution(ReadSource.UiaName, Finish(name, settings), SpeechKind.Label, false);
        }

        if (info.Kind == UiElementKind.Image)
        {
            var name = UsableName(info) ?? FirstUsableText(info.HelpText, info.FullDescription, info.LegacyDescription);
            if (name is null || IsFileName(name)) return null;
            return new UiResolution(ReadSource.UiaName, Finish(name, settings), SpeechKind.Label, false);
        }

        // 5. Controlli: Name, poi etichetta associata, HelpText, FullDescription, descrizioni legacy.
        var primary = UsableName(info);
        var source = ReadSource.UiaName;
        if (primary is not null && info.ParentKind == UiElementKind.SplitButton && IsSecondaryPart(primary) && FirstUsable(info.ParentName) is { } parentName)
            primary = $"{parentName}, {primary.ToLowerInvariant()}";

        // Identificatore minuscolo attaccato con un suggerimento disponibile (Affinity "newdocnew" con HelpText "Nuova"): il suggerimento.
        if (primary is not null && LowercaseIdentifier().IsMatch(primary) && FirstUsableText(info.HelpText, info.FullDescription) is { } description
            && !string.Equals(description, primary, StringComparison.OrdinalIgnoreCase))
        {
            primary = description;
            source = ReadSource.UiaDescription;
        }

        var chosen = primary;
        if (chosen is null)
        {
            chosen = FirstUsableText(info.LabeledByName);
            if (chosen is null)
            {
                // Anche i ripieghi possono contenere un nome di tipo .NET (separatore di Affinity: LegacyName = nome del tipo).
                chosen = FirstUsableText(info.HelpText, info.FullDescription, info.LegacyDescription, info.LegacyName);
                source = ReadSource.UiaDescription;
            }
        }
        // Separatore di menu senza nome utile (alto pochi pixel): silenzio.
        if (info.Kind == UiElementKind.MenuItem && primary is null && info.Bounds.Height is > 0 and <= SeparatorMaxHeight)
            chosen = null;
        if (chosen is null)
        {
            if (info.Kind == UiElementKind.Text && info.Value is { } v && v.Trim().Length > 0) return new UiResolution(ReadSource.UiaValue, Finish(v, settings), SpeechKind.Label, false);
            return null;
        }

        var cleaned = Finish(LabelCleaner.Clean(chosen, info.Kind, settings), settings);
        if (cleaned.Length == 0) return null;

        if (settings.SpeakToggleState && info.ToggleState != UiToggleState.None && info.Kind is UiElementKind.CheckBox or UiElementKind.MenuItem or UiElementKind.Button or UiElementKind.RadioButton)
        {
            cleaned += info.ToggleState switch
            {
                UiToggleState.On => ", attivo",
                UiToggleState.Off => ", non attivo",
                UiToggleState.Indeterminate => ", parzialmente attivo",
                _ => string.Empty,
            };
        }
        if (!info.IsEnabled && info.Kind is UiElementKind.MenuItem or UiElementKind.Button && settings.SpeakToggleState)
            cleaned += ", non disponibile";

        var kind = cleaned.Length > 80 || info.Kind is UiElementKind.Text or UiElementKind.StatusBar or UiElementKind.Hyperlink && cleaned.Length > 40
            ? SpeechKind.Sentence : SpeechKind.Label;
        return new UiResolution(source, cleaned, kind, Sensitive: false);
    }

    /// <summary>Il Name dell'elemento se è testo pronunciabile e non un nome di tipo, un identificatore o un ToString.</summary>
    internal static string? UsableName(UiElementInfo info)
    {
        var name = info.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();

        var m = ToStringWithTitle().Match(name);
        if (m.Success) return FirstUsableText(m.Groups["v"].Value);
        if (IsMachineText(name)) return null;
        if (name.Equals(info.ClassName, StringComparison.Ordinal) || name.Equals(info.AutomationId, StringComparison.Ordinal))
        {
            // Name uguale a ClassName/AutomationId è quasi sempre un identificatore, a meno che non sembri una parola vera.
            if (!name.Contains(' ') && !IsNaturalWord(name)) return null;
        }
        if (!name.Any(char.IsLetterOrDigit)) return null;
        return name;
    }

    /// <summary>Testo scritto per le macchine e non per le persone: nome di tipo .NET, ToString con graffe, identificatore di codice.</summary>
    private static bool IsMachineText(string text)
    {
        if (ToStringBraces().IsMatch(text)) return true;
        if (TypeName().IsMatch(text)) return true;
        return !text.Contains(' ') && Identifier().IsMatch(text) && text.Length > 6 && !text.Any(c => c is 'à' or 'è' or 'é' or 'ì' or 'ò' or 'ù');
    }

    /// <summary>Primo candidato pronunciabile che non sia testo per le macchine (nomi di tipo, identificatori, ToString).</summary>
    private static string? FirstUsableText(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            var t = FirstUsable(c);
            if (t is null || IsMachineText(t)) continue;
            var m = ToStringWithTitle().Match(t);
            if (m.Success)
            {
                var title = FirstUsable(m.Groups["v"].Value);
                if (title is null) continue;
                return title;
            }
            return t;
        }
        return null;
    }

    private static bool IsLongOrMultiline(string text, int maxChars) =>
        text.Length > maxChars || text.AsSpan().Trim().IndexOfAny('\n', '\r', '\v') >= 0;

    private static bool IsNaturalWord(string s) => s.Length <= 20 && s.All(char.IsLetter) && (s.All(char.IsLower) || (char.IsUpper(s[0]) && s.Skip(1).All(char.IsLower)));

    private static string? FirstUsable(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var t = c.Trim();
            if (ToStringBraces().IsMatch(t)) continue;
            if (!t.Any(char.IsLetterOrDigit)) continue;
            return t;
        }
        return null;
    }

    private static string Finish(string text, ReadingSettings settings)
    {
        var s = text.Trim();
        if (settings.StripEmoji) s = EmojiFilter.Strip(s);
        if (settings.MaxCharsPerRead > 0 && s.Length > settings.MaxCharsPerRead)
        {
            int cut = s.LastIndexOf(' ', settings.MaxCharsPerRead - 1);
            s = s[..(cut > settings.MaxCharsPerRead / 2 ? cut : settings.MaxCharsPerRead)].TrimEnd();
        }
        return s;
    }

    private static bool IsContainer(UiElementKind kind) => kind is UiElementKind.Pane or UiElementKind.Window or UiElementKind.Group or UiElementKind.Custom
        or UiElementKind.Document or UiElementKind.List or UiElementKind.Tree or UiElementKind.Tab or UiElementKind.ToolBar or UiElementKind.MenuBar
        or UiElementKind.TitleBar or UiElementKind.DataGrid or UiElementKind.Header or UiElementKind.Unknown or UiElementKind.ScrollBar;

    private static bool IsSecondaryPart(string name) => name.Equals("Altre opzioni", StringComparison.OrdinalIgnoreCase) || name.Equals("More options", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Opzioni", StringComparison.OrdinalIgnoreCase) || name.Equals("Menu", StringComparison.OrdinalIgnoreCase) || name.Equals("Espandi", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\$?[A-Z]{1,3}\$?\d{1,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex CellReference();
    private static bool LooksLikeCellReference(string? name) => name is not null && CellReference().IsMatch(name.Trim());

    [GeneratedRegex(@"^[\w\-. ]+\.(png|jpe?g|gif|bmp|svg|ico|webp|tiff?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FileName();
    private static bool IsFileName(string s) => FileName().IsMatch(s.Trim());
}
