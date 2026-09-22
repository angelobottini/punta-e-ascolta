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
