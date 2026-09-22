using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Reading;
using PuntaEAscolta.Logic.Resolution;
using PuntaEAscolta.Windows.Automation;
using Xunit;

namespace PuntaEAscolta.Windows.Tests;

/// <summary>
/// Rifinitura, voce 2 della revisione: se le chiamate sugli intervalli di testo falliscono, un elemento con TextPattern vale
/// "puntatore fuori dal testo" e una pagina di Word non dice più il proprio nome ("Contenuto pagina 1").
/// </summary>
public class TextContextFailureTests
{
    private static readonly ReadingSettings Default = new();

    [Fact]
    public void ElementWithTextPattern_FailureMeansPointerOffText()
    {
        var context = ElementReader.TextContextAfterFailure(hasTextPattern: true);

        Assert.NotNull(context);
        Assert.False(context!.PointerOverText);
        Assert.Equal(string.Empty, context.ParagraphText);
    }

    [Fact]
    public void ElementWithoutTextPattern_FailureMeansNoContext()
    {
        Assert.Null(ElementReader.TextContextAfterFailure(hasTextPattern: false));
    }

    [Fact]
    public void WordPage_WhoseRangesFail_DoesNotSpeakThePageName()
    {
        var page = new UiElementInfo
        {
            Kind = UiElementKind.Edit,
            Name = "Contenuto pagina 1",
            Bounds = new ScreenRect(356, 180, 1020, 1320),
            Text = ElementReader.TextContextAfterFailure(hasTextPattern: true),
        };

        Assert.Null(UiTextResolver.Resolve(page, Default));
    }

    [Fact]
    public void Link_WhoseDocumentRangesFail_KeepsItsName()
    {
        // Il collegamento non ha il TextPattern (ce l'ha il documento del browser): nome tenuto, come voleva la voce 4.
        var link = new UiElementInfo
        {
            Kind = UiElementKind.Hyperlink,
            Name = "Accedi",
            Bounds = new ScreenRect(900, 120, 80, 24),
            Text = ElementReader.TextContextAfterFailure(hasTextPattern: false),
        };

        var resolution = UiTextResolver.Resolve(link, Default);

        Assert.Equal("Accedi", resolution!.Text);
        Assert.Equal(ReadSource.UiaName, resolution.Source);
    }
}

/// <summary>
/// Rifinitura, BUG 10 delle prove dal vivo: le icone della barra superiore di Affinity sono ToolBar senza nome, una per
/// icona, con il nome ripetuto nei Text figli (dati di docs/research/prove-dal-vivo-2.md).
/// </summary>
public class IconToolbarNameTests
{
    [Theory]
    [InlineData(46, 34, true)]     // "Modalità anteprima", "Disponi", "Guida"...
    [InlineData(64, 34, true)]     // "Effetto calamita" con la freccia della tendina
    [InlineData(101, 43, true)]    // ToolBar di sinistra con due icone (poi scartata per i nomi diversi)
    [InlineData(16, 34, true)]     // separatore: nessun Text figlio, quindi nessun nome
    [InlineData(723, 34, false)]   // spazio vuoto centrale
    [InlineData(1920, 40, false)]  // barra intera
    [InlineData(200, 200, false)]  // pannello
    [InlineData(0, 0, false)]      // rettangolo vuoto
    public void IsIconSizedToolbar(int w, int h, bool expected)
    {
        Assert.Equal(expected, ElementReader.IsIconSizedToolbar(new ScreenRect(1341, 57, w, h)));
    }

    [Theory]
    [InlineData("Modalità anteprima")]
    [InlineData("Effetto calamita")]
    [InlineData("Guida")]
    public void SameNameInEveryText_IsTheIconName(string name)
    {
        // Text con AutomationId 'text' (fuori schermo) e TextBlock con lo stesso nome; il Button della tendina non è un Text.
        Assert.Equal(name, ElementReader.IconToolbarName(new[] { name, name }));
    }

    [Fact]
    public void DifferentNames_AreAmbiguous_NoName()
    {
        // ToolBar di sinistra: due icone, nessun rettangolo per sapere quale è sotto il puntatore.
        var names = new[] { "Modalità di visualizzazione Vettore", "Modalità di visualizzazione Pixel" };
        Assert.Null(ElementReader.IconToolbarName(names));
    }

    [Fact]
    public void NoTexts_NoName()
    {
        Assert.Null(ElementReader.IconToolbarName(Array.Empty<string>()));
        Assert.Null(ElementReader.IconToolbarName(new[] { " ", "" }));
    }

    [Fact]
    public void NamedIconToolbar_IsSpokenByTheResolver()
    {
        var info = new UiElementInfo { Kind = UiElementKind.ToolBar, Name = "Modalità anteprima", Bounds = new ScreenRect(1341, 57, 46, 34), IsEnabled = false };

        var resolution = UiTextResolver.Resolve(info, new ReadingSettings());

        Assert.Equal("Modalità anteprima", resolution!.Text);
        Assert.Equal(SpeechKind.Label, resolution.Kind);
    }
}

/// <summary>Rifinitura: le finestre candidate a suggerimento si filtrano per posizione già nello strato UIA, con la regola della logica.</summary>
public class TooltipPlacementTests
{
    private static readonly ScreenPoint P = new(1000, 500);

    [Fact]
    public void Constants_MatchTheResolver()
    {
        Assert.Equal(TextResolver.TooltipMaxAbovePx, ElementReader.TooltipMaxAbovePx);
        Assert.Equal(TextResolver.TooltipMaxBelowPx, ElementReader.TooltipMaxBelowPx);
        Assert.Equal(TextResolver.TooltipMaxRightOfPointerPx, ElementReader.TooltipMaxRightOfPointerPx);
        Assert.Equal(TextResolver.TooltipMaxLeftOfPointerPx, ElementReader.TooltipMaxLeftOfPointerPx);
    }

    // Stessi casi di TextResolverTests.IsPlacedLikeTooltip_Geometry.
    [Theory]
    [InlineData(1010, 530, 120, 24, 1.0, true)]
    [InlineData(1000, 490, 120, 24, 1.0, true)]
    [InlineData(1000, 489, 120, 24, 1.0, false)]
    [InlineData(1000, 600, 120, 24, 1.0, true)]
    [InlineData(1000, 601, 120, 24, 1.0, false)]
    [InlineData(1000, 620, 120, 24, 1.25, true)]
    [InlineData(1060, 520, 120, 24, 1.0, true)]
    [InlineData(1061, 520, 120, 24, 1.0, false)]
    [InlineData(600, 520, 100, 24, 1.0, true)]
    [InlineData(599, 520, 100, 24, 1.0, false)]
    [InlineData(1010, 530, 0, 0, 1.0, false)]
    public void IsPlacedLikeTooltip_Geometry(int x, int y, int w, int h, double dpi, bool expected)
    {
        Assert.Equal(expected, ElementReader.IsPlacedLikeTooltip(new ScreenRect(x, y, w, h), P, dpi));
    }
}
