using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Text;
using Xunit;

namespace PuntaEAscolta.Logic.Tests.Text;

public class LabelCleanerTests
{
    private static readonly ReadingSettings Default = new();

    [Theory]
    [InlineData("&File", "File")]
    [InlineData("_Chiudi", "Chiudi")]
    [InlineData("File(&F)", "File")]
    [InlineData("Salva && esci", "Salva & esci")]
    [InlineData("Nuovo\tCtrl+N", "Nuovo")]
    [InlineData("Salva con nome...\tCtrl+Maiusc+S", "Salva con nome")]
    [InlineData("Esci  Alt+F4", "Esci")]
    [InlineData("Incolla Ctrl+V", "Incolla")]
    [InlineData("Apri...", "Apri")]
    [InlineData("Esporta…", "Esporta")]
    [InlineData("Apri Recenti ▸", "Apri Recenti")]
    [InlineData("▸ Livelli", "Livelli")]
    [InlineData("  Spazi   multipli  ", "Spazi multipli")]
    [InlineData("Guida\tF1", "Guida")]
    [InlineData("Snap to grid", "Snap to grid")]
    public void Clean_MenuLabels(string raw, string expected)
    {
        Assert.Equal(expected, LabelCleaner.Clean(raw, UiElementKind.MenuItem, Default));
    }

    [Fact]
    public void Clean_UnderscoreInsideWordIsKept()
    {
        Assert.Equal("file_name", LabelCleaner.Clean("file_name", UiElementKind.Text, Default));
        Assert.Equal("Snake case", LabelCleaner.Clean("_Snake case", UiElementKind.Button, Default));
    }

    [Fact]
    public void Clean_KeepsShortcutWhenSettingDisabled()
    {
        var s = new ReadingSettings { StripKeyboardShortcuts = false };
        Assert.Equal("Nuovo Ctrl+N", LabelCleaner.Clean("Nuovo\tCtrl+N", UiElementKind.MenuItem, s));
    }

    [Fact]
    public void Clean_StripsEmoji()
    {
        Assert.Equal("Preferiti", LabelCleaner.Clean("⭐ Preferiti", UiElementKind.Button, Default));
    }

    [Theory]
    [InlineData("Ctrl+N", true)]
    [InlineData("CtrI+N", true)]
    [InlineData("Ctr1 + Maiusc + S", true)]
    [InlineData("Ctd+O", true)]
    [InlineData("AIt+F4", true)]
    [InlineData("Ctrl + p", true)]
    [InlineData("Ctrl+Alt+VV/", true)]
    [InlineData("Maiusc+Ctrl+Alt+S", true)]
    [InlineData("F5", true)]
    [InlineData("F12", true)]
    [InlineData("Ctrl+Maiusc+Canc", true)]
    [InlineData("⌘N", true)]
    [InlineData("Nuovo", false)]
    [InlineData("Controllo ortografico", false)]
    [InlineData("Altre opzioni", false)]
    [InlineData("Fine", false)]
    [InlineData("Alternativa", false)]
    public void IsKeyboardShortcut(string text, bool expected)
    {
        Assert.Equal(expected, LabelCleaner.IsKeyboardShortcut(text));
    }

    [Theory]
    [InlineData("Nuovo Ctrl+N", "Nuovo")]
    [InlineData("Apri Recenti >", "Apri Recenti")]
    [InlineData("> Esporta", "Esporta")]
    [InlineData("v Layout", "Layout")]
    [InlineData("Ctrl+N", "")]
    [InlineData("CtrI+Maiusc+S", "")]
    [InlineData("\\", "")]
    [InlineData("—", "")]
    [InlineData("Salva con nome...", "Salva con nome")]
    [InlineData("ATTENZIONE: È VIETATO L'ACCESSO", "ATTENZIONE: È VIETATO L'ACCESSO")]
    [InlineData("Opacità: 100 %", "Opacità: 100 %")]
    [InlineData("S: 100", "S: 100")]
    public void CleanOcrLine(string line, string expected)
    {
        Assert.Equal(expected, LabelCleaner.CleanOcrLine(line, Default));
    }
}
