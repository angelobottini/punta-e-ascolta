using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Resolution;
using Xunit;

namespace PuntaEAscolta.Logic.Tests.Resolution;

public class PointerTextSelectorTests
{
    private static readonly OcrSettings Default = new();

    /// <summary>Costruisce una riga OCR con parole disposte da x in avanti; gap = spazio fra le parole in px.</summary>
    private static OcrLine Line(double x, double y, double h, double gap, params string[] words)
    {
        var list = new List<OcrWord>();
        double cx = x;
        foreach (var w in words)
        {
            double width = w.Length * h * 0.55;
            list.Add(new OcrWord(w, new ImageRect(cx, y, width, h)));
            cx += width + gap;
        }
        var box = new ImageRect(x, y, list[^1].Box.Right - x, h);
        return new OcrLine(string.Join(' ', words), box, list);
    }

    /// <summary>Riga di menu come la restituisce Windows OCR: etichetta a sinistra e scorciatoia a destra fuse nella stessa riga.</summary>
    private static OcrLine MenuRow(double y, string label, string? shortcut)
    {
        const double h = 12;
        var words = new List<OcrWord>();
        double cx = 20;
        foreach (var w in label.Split(' '))
        {
            double width = w.Length * h * 0.55;
            words.Add(new OcrWord(w, new ImageRect(cx, y, width, h)));
            cx += width + 4;
        }
        if (shortcut is not null)
            words.Add(new OcrWord(shortcut, new ImageRect(380, y, shortcut.Length * h * 0.55, h)));
        var text = shortcut is null ? label : label + " " + shortcut;
        return new OcrLine(text, new ImageRect(20, y, words[^1].Box.Right - 20, h), words);
    }

    private static OcrResult AffinityFileMenu() => new(new[]
    {
        MenuRow(40, "Nuovo", "Ctrl+N"),
        MenuRow(73, "Apri", "CtrI+O"),
        MenuRow(106, "Apri Recenti", ">"),
        MenuRow(139, "Chiudi", "Ctrl+W"),
        MenuRow(172, "Salva", "Ctd+S"),
        MenuRow(205, "Salva con nome...", "Ctrl+Maiusc+S"),
        MenuRow(238, "Esporta...", "Ctrl+AIt+Maiusc+S"),
        MenuRow(271, "Esci", "Alt+F4"),
    }, "windows", 40);

    [Fact]
    public void MenuRow_PointerOnLabel_ReadsOnlyLabel()
    {
        var r = PointerTextSelector.Select(AffinityFileMenu(), 40, 46, Default, false);
        Assert.NotNull(r);
        Assert.Equal("Nuovo", r!.Text);
        Assert.Equal(ReadSource.OcrLine, r.Source);
    }

    [Fact]
    public void MenuRow_PointerOnShortcut_ReadsLabelOnTheLeft()
    {
        var r = PointerTextSelector.Select(AffinityFileMenu(), 400, 178, Default, false);
        Assert.Equal("Salva", r!.Text);
    }

    [Fact]
    public void MenuRow_PointerInEmptyPartOfRow_ReadsNearestLabelLeft()
    {
        var r = PointerTextSelector.Select(AffinityFileMenu(), 250, 112, Default, false);
        Assert.Equal("Apri Recenti", r!.Text);
    }

    [Fact]
    public void MenuRow_TrailingDotsAndOcrShortcutsAreStripped()
    {
        Assert.Equal("Salva con nome", PointerTextSelector.Select(AffinityFileMenu(), 60, 211, Default, false)!.Text);
        Assert.Equal("Esporta", PointerTextSelector.Select(AffinityFileMenu(), 60, 244, Default, false)!.Text);
    }

    [Fact]
    public void MenuRows_AreNotGroupedIntoBlocks()
    {
        var r = PointerTextSelector.Select(AffinityFileMenu(), 40, 145, Default, false);
        Assert.Equal(ReadSource.OcrLine, r!.Source);
        Assert.Equal("Chiudi", r.Text);
    }

    [Fact]
    public void PointerBetweenRows_PicksNearestWithinTolerance()
    {
        // y=60 è fra "Nuovo" (40-52) e "Apri" (73-85): distanza 8 px da Nuovo, 13 da Apri
        var r = PointerTextSelector.Select(AffinityFileMenu(), 40, 60, Default, false);
        Assert.Equal("Nuovo", r!.Text);
    }

    [Fact]
    public void PointerFarFromAnyLine_ReturnsNull()
    {
        Assert.Null(PointerTextSelector.Select(AffinityFileMenu(), 40, 600, Default, false));
    }

    [Fact]
    public void Sign_StackedLines_ReadAsOneBlock()
    {
        const double h = 40;
        var result = new OcrResult(new[]
        {
            Line(100, 100, h, 12, "ATTENZIONE:"),
            Line(100, 150, h, 12, "È", "VIETATO"),
            Line(100, 200, h, 12, "L'ACCESSO"),
        }, "onnx", 90);
        var r = PointerTextSelector.Select(result, 150, 170, Default, false);
        Assert.Equal(ReadSource.OcrBlock, r!.Source);
        Assert.Equal("ATTENZIONE: È VIETATO L'ACCESSO", r.Text);
        Assert.Equal(3, r.LineCount);
    }

    [Fact]
    public void Paragraph_HyphenationIsRejoined()
    {
        const double h = 14;
        var result = new OcrResult(new[]
        {
            Line(10, 10, h, 4, "Questa", "è", "una", "informa-"),
            Line(10, 27, h, 4, "zione", "importante."),
        }, "windows", 30);
        var r = PointerTextSelector.Select(result, 30, 16, Default, false);
        Assert.Equal("Questa è una informazione importante.", r!.Text);
    }

    [Fact]
    public void GroupingDisabled_ReadsSingleLine()
    {
        var s = new OcrSettings { GroupLinesIntoBlocks = false };
        var result = new OcrResult(new[] { Line(100, 100, 40, 12, "ATTENZIONE:"), Line(100, 150, 40, 12, "È", "VIETATO") }, "onnx", 90);
        var r = PointerTextSelector.Select(result, 150, 120, s, false);
        Assert.Equal("ATTENZIONE:", r!.Text);
    }

    [Fact]
    public void WholeZone_ReadsAllRowsTopToBottom_WithoutShortcuts()
    {
        var r = PointerTextSelector.Select(AffinityFileMenu(), 0, 0, Default, wholeZone: true);
        Assert.Equal(ReadSource.OcrZone, r!.Source);
        Assert.Equal(8, r.LineCount);
        Assert.StartsWith("Nuovo. Apri. Apri Recenti", r.Text);
        Assert.DoesNotContain("Ctrl", r.Text);
    }

    [Fact]
    public void PanelTabs_MergedLine_IsSplitAtWideGaps()
    {
        // Schede di un pannello fuse in una riga: "Colore  Campioni  Pennelli" con spazi di circa 1 altezza
        const double h = 12;
        var result = new OcrResult(new[] { Line(20, 20, h, 11, "Colore", "Campioni", "Pennelli") }, "windows", 20);
        var campioniX = 20 + 6 * h * 0.55 + 11 + 3;
        Assert.Equal("Campioni", PointerTextSelector.Select(result, campioniX, 26, Default, false)!.Text);
        Assert.Equal("Colore", PointerTextSelector.Select(result, 25, 26, Default, false)!.Text);
    }

    [Fact]
    public void SingleSymbolSegments_AreIgnored()
    {
        const double h = 12;
        var result = new OcrResult(new[] { Line(20, 20, h, 10, "v", "Layout") }, "windows", 20);
        Assert.Equal("Layout", PointerTextSelector.Select(result, 22, 26, Default, false)!.Text);
    }

    [Fact]
    public void ClipTo_RestrictsCandidates()
    {
        var clip = new ImageRect(0, 130, 500, 30);
        var r = PointerTextSelector.Select(AffinityFileMenu(), 40, 145, Default, false, clip);
        Assert.Equal("Chiudi", r!.Text);
        var none = PointerTextSelector.Select(AffinityFileMenu(), 40, 46, Default, false, clip);
        Assert.Null(none);
    }

    [Fact]
    public void EmptyResult_ReturnsNull()
    {
        Assert.Null(PointerTextSelector.Select(OcrResult.Empty("windows"), 10, 10, Default, false));
    }

    /// <summary>Prova dal vivo 6: cartello in una finestra davanti alla pagina di un browser, righe a Y intercalate.</summary>
    private static OcrResult SignInFrontOfBrowser() => new(new[]
    {
        Line(100, 100, 40, 12, "ATTENZIONE:"),
        Line(100, 150, 40, 12, "È", "VIETATO"),
        Line(100, 200, 40, 12, "L'ACCESSO"),
        Line(600, 95, 14, 4, "Prima", "riga", "del", "browser"),
        Line(600, 112, 14, 4, "Seconda", "riga", "del", "browser"),
        Line(600, 129, 14, 4, "Terza", "riga", "del", "browser"),
    }, "windows", 50);

    [Fact]
    public void WholeZone_ColumnsAreNotInterleaved_PointerBlockFirst()
    {
        var r = PointerTextSelector.Select(SignInFrontOfBrowser(), 150, 170, Default, wholeZone: true);
        Assert.Equal(ReadSource.OcrZone, r!.Source);
        Assert.Equal(6, r.LineCount);
        Assert.StartsWith("ATTENZIONE: È VIETATO. L'ACCESSO", r.Text);
        Assert.EndsWith("Prima riga del browser. Seconda riga del browser. Terza riga del browser", r.Text);
    }

    [Fact]
    public void WholeZone_PointerOnOtherColumn_ReadsThatColumnFirst()
    {
        var r = PointerTextSelector.Select(SignInFrontOfBrowser(), 650, 118, Default, wholeZone: true);
        Assert.StartsWith("Prima riga del browser. Seconda riga del browser. Terza riga del browser. ATTENZIONE:", r!.Text);
    }

    [Fact]
    public void WholeZone_WrappedParagraphLinesAreJoinedWithoutPause()
    {
        const double h = 14;
        var result = new OcrResult(new[]
        {
            Line(10, 10, h, 4, "Questa", "è", "una", "frase"),
            Line(10, 27, h, 4, "che", "va", "a", "capo."),
        }, "windows", 30);
        var r = PointerTextSelector.Select(result, 30, 16, Default, wholeZone: true);
        Assert.Equal("Questa è una frase che va a capo.", r!.Text);
    }

    [Fact]
    public void MenuRow_LabelLikeModifierWord_IsNotTakenForShortcut()
    {
        // "Allineamento      Alto": prima "Alto" era scambiato per la scorciatoia Alt+o e si leggeva l'etichetta a sinistra.
        const double h = 12;
        var words = new[] { new OcrWord("Allineamento", new ImageRect(10, 100, 110, h)), new OcrWord("Alto", new ImageRect(300, 100, 40, h)) };
        var result = new OcrResult(new[] { new OcrLine("Allineamento Alto", new ImageRect(10, 100, 330, h), words) }, "windows", 10);
        Assert.Equal("Alto", PointerTextSelector.Select(result, 320, 106, Default, false)!.Text);
        var single = new OcrResult(new[] { new OcrLine("Altro", new ImageRect(10, 100, 50, h), new[] { new OcrWord("Altro", new ImageRect(10, 100, 50, h)) }) }, "windows", 10);
        Assert.Equal("Altro", PointerTextSelector.Select(single, 30, 106, Default, false)!.Text);
    }
}
