using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Resolution;
using Xunit;

namespace PuntaEAscolta.Logic.Tests.Resolution;

public class UiTextResolverTests
{
    private static readonly ReadingSettings Default = new();
    private static UiElementInfo El(UiElementKind kind, string? name, int w = 100, int h = 24) => new() { Kind = kind, Name = name, Bounds = new ScreenRect(0, 0, w, h) };

    [Fact]
    public void MenuItem_NameWithAccessKeyMarker()
    {
        var r = UiTextResolver.Resolve(El(UiElementKind.MenuItem, "_Chiudi"), Default);
        Assert.NotNull(r);
        Assert.Equal("Chiudi", r!.Text);
        Assert.Equal(ReadSource.UiaName, r.Source);
        Assert.Equal(SpeechKind.Label, r.Kind);
        Assert.False(r.Sensitive);
    }

    [Fact]
    public void MenuItem_NamedLikeTypeIsUnusable()
    {
        Assert.Null(UiTextResolver.Resolve(El(UiElementKind.MenuItem, "Serif.Affinity.Workspaces.WorkspaceMenuSeparator"), Default));
        Assert.Null(UiTextResolver.Resolve(El(UiElementKind.ListItem, "Serif.Affinity.Workspaces.Workspace"), Default));
        Assert.Null(UiTextResolver.Resolve(El(UiElementKind.Pane, "PaneClassDC", 50, 20), Default));
    }

    [Fact]
    public void TabItem_ToStringWithTitle_ExtractsTitle()
    {
        var r = UiTextResolver.Resolve(El(UiElementKind.TabItem, "StudioPage, Title = Colore"), Default);
        Assert.Equal("Colore", r!.Text);
    }

    [Fact]
    public void Button_EmptyName_FallsBackToHelpText()
    {
        var info = El(UiElementKind.Button, "") with { HelpText = "Strumento Penna" };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal("Strumento Penna", r!.Text);
        Assert.Equal(ReadSource.UiaDescription, r.Source);
    }

    [Fact]
    public void Button_NothingUsable_ReturnsNull()
    {
        Assert.Null(UiTextResolver.Resolve(El(UiElementKind.Button, ""), Default));
        Assert.Null(UiTextResolver.Resolve(El(UiElementKind.Button, null), Default));
    }

    [Fact]
    public void SplitButton_SecondaryPart_SpeaksParentName()
    {
        var info = El(UiElementKind.Button, "Altre opzioni") with { ParentKind = UiElementKind.SplitButton, ParentName = "Incolla" };
        Assert.Equal("Incolla, altre opzioni", UiTextResolver.Resolve(info, Default)!.Text);
    }

    [Fact]
    public void ExcelCell_SpeaksValueNotName()
    {
        var info = El(UiElementKind.DataItem, "C3") with { Value = "1.234,50" };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal("1.234,50", r!.Text);
        Assert.Equal(ReadSource.UiaValue, r.Source);
    }

    [Fact]
    public void ExcelCell_WithTextContext_StillSpeaksValue()
    {
        var info = El(UiElementKind.DataItem, "C3") with
        {
            Value = "Totale",
            Text = new UiTextContext("Totale", 2, PointerOverText: true),
        };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal("Totale", r!.Text);
        Assert.Equal(ReadSource.UiaValue, r.Source);
    }

    [Fact]
    public void Document_OnlyObjectReplacementCharacters_ReturnsNull()
    {
        var info = El(UiElementKind.Document, "Documento", 800, 1000) with
        {
            Text = new UiTextContext(new string((char)0xFFFC, 3), 1, PointerOverText: true),
        };
        Assert.Null(UiTextResolver.Resolve(info, Default));
    }

    [Fact]
    public void ExcelEmptyCell_SpeaksEmptyCellText()
    {
        var info = El(UiElementKind.DataItem, "D4") with { Value = "" };
        Assert.Equal("Cella vuota", UiTextResolver.Resolve(info, Default)!.Text);
        var custom = new ReadingSettings { EmptyCellText = "vuota" };
        Assert.Equal("vuota", UiTextResolver.Resolve(info, custom)!.Text);
    }

    [Fact]
    public void WordDocument_WithPointedText_ReturnsSentence()
    {
        var info = El(UiElementKind.Edit, "Contenuto pagina 1", 800, 1000) with
        {
            Text = new UiTextContext("Prima frase. Seconda frase con il puntatore. Terza.\r", 20, PointerOverText: true),
        };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal("Seconda frase con il puntatore.", r!.Text);
        Assert.Equal(ReadSource.UiaSentence, r.Source);
        Assert.Equal(SpeechKind.Sentence, r.Kind);
        Assert.True(r.Sensitive);
    }

    [Fact]
    public void WordDocument_PointerNotOverText_ReturnsNullOnLargeContainer()
    {
        var info = El(UiElementKind.Document, "Documento1", 800, 1000) with { Text = new UiTextContext("Testo lontano.", 0, PointerOverText: false) };
        Assert.Null(UiTextResolver.Resolve(info, Default));
    }

    [Fact]
    public void Document_SentenceDisabledBySetting_DoesNotReadSentence()
    {
        var s = new ReadingSettings { ReadSentenceInDocuments = false };
        var info = El(UiElementKind.Document, "Documento1", 800, 1000) with { Text = new UiTextContext("Testo.", 0, true) };
        Assert.Null(UiTextResolver.Resolve(info, s));
    }

    [Fact]
    public void LargePaneWithoutName_ReturnsNull()
    {
        Assert.Null(UiTextResolver.Resolve(El(UiElementKind.Pane, "", 400, 300), Default));
        Assert.Null(UiTextResolver.Resolve(El(UiElementKind.Tab, "", 329, 962), Default));
        Assert.Null(UiTextResolver.Resolve(El(UiElementKind.Window, "Affinity", 1920, 1200), Default));
    }

    [Fact]
    public void SmallGroupWithName_IsSpoken()
    {
        Assert.Equal("Colore", UiTextResolver.Resolve(El(UiElementKind.Group, "Colore", 120, 30), Default)!.Text);
    }

    [Fact]
    public void Image_WithoutName_ReturnsNull_WithFileNameReturnsNull()
    {
        Assert.Null(UiTextResolver.Resolve(El(UiElementKind.Image, ""), Default));
        Assert.Null(UiTextResolver.Resolve(El(UiElementKind.Image, "foto_123.jpg"), Default));
        Assert.Equal("Logo aziendale", UiTextResolver.Resolve(El(UiElementKind.Image, "Logo aziendale"), Default)!.Text);
    }

    [Fact]
    public void PasswordEdit_NeverSpeaksValue()
    {
        var info = El(UiElementKind.Edit, "Password") with { Value = "segreto", IsPassword = true };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal("Password", r!.Text);
    }

    [Fact]
    public void Edit_SpeaksLabelAndValue()
    {
        var info = El(UiElementKind.Edit, "Nome file") with { Value = "relazione.docx" };
        Assert.Equal("Nome file, relazione.docx", UiTextResolver.Resolve(info, Default)!.Text);
    }

    [Fact]
    public void CheckBox_ToggleStateOnlyWhenEnabled()
    {
        var info = El(UiElementKind.CheckBox, "Mostra griglia") with { ToggleState = UiToggleState.On };
        Assert.Equal("Mostra griglia", UiTextResolver.Resolve(info, Default)!.Text);
        var s = new ReadingSettings { SpeakToggleState = true };
        Assert.Equal("Mostra griglia, attivo", UiTextResolver.Resolve(info, s)!.Text);
        Assert.Equal("Mostra griglia, non attivo", UiTextResolver.Resolve(info with { ToggleState = UiToggleState.Off }, s)!.Text);
    }

    [Fact]
    public void StatusBarLongText_IsSentence()
    {
        var info = El(UiElementKind.Text, "Pagina 3 di 12, 1.234 parole, italiano, controllo ortografico completato senza errori", 600, 20);
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal(SpeechKind.Sentence, r!.Kind);
    }

    [Fact]
    public void Name_WithEmojiAndShortcut_IsCleaned()
    {
        var info = El(UiElementKind.MenuItem, "⭐ Preferiti\tCtrl+D");
        Assert.Equal("Preferiti", UiTextResolver.Resolve(info, Default)!.Text);
    }

    [Fact]
    public void MaxChars_TruncatesAtWordBoundary()
    {
        var s = new ReadingSettings { MaxCharsPerRead = 30 };
        var info = El(UiElementKind.Text, "Questo è un testo molto lungo che deve essere troncato a una parola intera", 600, 20);
        var text = UiTextResolver.Resolve(info, s)!.Text;
        Assert.True(text.Length <= 30);
        Assert.False(text.EndsWith(' '));
        Assert.StartsWith("Questo è un testo", text);
    }
}

/// <summary>Correzioni della revisione e delle prove dal vivo del 22/09/2026 (docs/impl-notes/revisione.md).</summary>
public class UiTextResolverReviewTests
{
    private static readonly ReadingSettings Default = new();
    private static UiElementInfo El(UiElementKind kind, string? name, int w = 100, int h = 24) => new() { Kind = kind, Name = name, Bounds = new ScreenRect(0, 0, w, h) };

    private const string Body = "Ciao Marco, ti scrivo per la riunione di domani.\nHo preparato i documenti.\n\nA presto, Matteo.";

    [Fact]
    public void WordEmptyPageArea_PageNameIsNotSpoken()
    {
        // Word: Edit "Contenuto pagina 1" con TextPattern; puntatore sotto l'ultimo paragrafo o sul margine grigio.
        var info = El(UiElementKind.Edit, "Contenuto pagina 1", 816, 1056) with { Text = new UiTextContext(string.Empty, 0, PointerOverText: false) };
        Assert.Null(UiTextResolver.Resolve(info, Default));
    }

    [Fact]
    public void LargeMultilineEdit_PointerOffText_NeverReadsWholeDocument()
    {
        var withPattern = El(UiElementKind.Edit, "Messaggio", 600, 300) with { Value = Body, Text = new UiTextContext(string.Empty, 0, PointerOverText: false) };
        Assert.Null(UiTextResolver.Resolve(withPattern, Default));
        var withoutPattern = withPattern with { Text = null };
        Assert.Null(UiTextResolver.Resolve(withoutPattern, Default));
    }

    [Fact]
    public void SmallEdit_WithMultilineValue_SpeaksOnlyTheLabel()
    {
        var info = El(UiElementKind.Edit, "Note", 200, 40) with { Value = Body };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal("Note", r!.Text);
        Assert.Equal(ReadSource.UiaName, r.Source);
    }

    [Fact]
    public void SmallEdit_PointerOffText_StillSpeaksLabelAndShortValue()
    {
        var info = El(UiElementKind.Edit, "Cerca", 250, 30) with { Value = "gatti", Text = new UiTextContext(string.Empty, 0, PointerOverText: false) };
        Assert.Equal("Cerca, gatti", UiTextResolver.Resolve(info, Default)!.Text);
    }

    [Fact]
    public void ExplorerFileNameLabel_SpeaksOnlyTheFileName()
    {
        var info = El(UiElementKind.Edit, "Nome", 90, 36) with { Value = "lib.ps1", ParentKind = UiElementKind.ListItem, ParentName = "lib.ps1" };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal("lib.ps1", r!.Text);
        Assert.Equal(ReadSource.UiaValue, r.Source);
    }

    [Fact]
    public void ExplorerDetailsColumn_SpeaksColumnAndValue()
    {
        var info = El(UiElementKind.Edit, "Ultima modifica", 140, 22) with { Value = "22/09/2026 20:12", ParentKind = UiElementKind.ListItem, ParentName = "lib.ps1" };
        Assert.Equal("Ultima modifica, 22/09/2026 20:12", UiTextResolver.Resolve(info, Default)!.Text);
    }

    [Fact]
    public void ComboBox_SpeaksLabelAndValue()
    {
        var info = El(UiElementKind.ComboBox, "Dimensione carattere", 60, 24) with { Value = "12" };
        Assert.Equal("Dimensione carattere, 12", UiTextResolver.Resolve(info, Default)!.Text);
    }

    [Fact]
    public void ExcelActiveCell_PointerAwayFromText_SpeaksValue()
    {
        var info = El(UiElementKind.DataItem, "A1", 80, 20) with { Value = "Nome", Text = new UiTextContext(string.Empty, 0, PointerOverText: false) };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal("Nome", r!.Text);
        Assert.Equal(ReadSource.UiaValue, r.Source);
    }

    [Fact]
    public void WordTableCell_WithLongParagraph_ReadsSentenceNotWholeCell()
    {
        string cell = "Prima frase della cella con parecchie parole per superare il limite. Seconda frase, quella sotto il puntatore, anche lei piuttosto lunga.";
        int offset = cell.IndexOf("Seconda", StringComparison.Ordinal) + 3;
        var info = El(UiElementKind.DataItem, null, 400, 60) with { Value = cell, Text = new UiTextContext(cell, offset, PointerOverText: true) };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal(ReadSource.UiaSentence, r!.Source);
        Assert.StartsWith("Seconda frase", r.Text);
    }

    [Fact]
    public void AffinityMenuSeparator_TypeNameInLegacyName_IsSilent()
    {
        var thin = El(UiElementKind.MenuItem, null, 478, 6) with { LegacyName = "Serif.Affinity.Workspaces.WorkspaceMenuSeparator" };
        Assert.Null(UiTextResolver.Resolve(thin, Default));
        var tall = El(UiElementKind.MenuItem, null, 478, 30) with { LegacyName = "Serif.Affinity.Workspaces.WorkspaceMenuSeparator", HelpText = "Serif.Affinity.Workspaces.WorkspaceMenuSeparator" };
        Assert.Null(UiTextResolver.Resolve(tall, Default));
    }

    [Fact]
    public void LegacyName_WithRealText_IsStillUsedAsFallback()
    {
        var info = El(UiElementKind.Button, null) with { LegacyName = "Pennello" };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal("Pennello", r!.Text);
        Assert.Equal(ReadSource.UiaDescription, r.Source);
    }

    [Fact]
    public void LowercaseIdentifierName_PrefersHelpText()
    {
        var info = El(UiElementKind.Button, "newdocnew", 40, 40) with { HelpText = "Nuova" };
        var r = UiTextResolver.Resolve(info, Default);
        Assert.Equal("Nuova", r!.Text);
        Assert.Equal(ReadSource.UiaDescription, r.Source);
        Assert.Equal("Apri", UiTextResolver.Resolve(El(UiElementKind.Button, "newdocopen", 40, 40) with { HelpText = "Apri" }, Default)!.Text);
    }

    [Fact]
    public void NormalName_IsNotReplacedByHelpText()
    {
        var info = El(UiElementKind.Button, "Salva") with { HelpText = "Salva il documento" };
        Assert.Equal("Salva", UiTextResolver.Resolve(info, Default)!.Text);
        var word = El(UiElementKind.Button, "stampa") with { HelpText = "Stampa il documento" };
        Assert.Equal("stampa", UiTextResolver.Resolve(word, Default)!.Text);
    }
}
