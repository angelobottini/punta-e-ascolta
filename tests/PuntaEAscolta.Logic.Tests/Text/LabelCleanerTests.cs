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

/// <summary>Correzioni della revisione del 22/09/2026 (docs/impl-notes/revisione.md).</summary>
public class LabelCleanerReviewTests
{
    private static readonly ReadingSettings Default = new();

    [Theory]
    [InlineData("Grassetto (CTRL+G)", "Grassetto")]
    [InlineData("Corsivo (CTRL+I)", "Corsivo")]
    [InlineData("Barrato (Ctrl+MAIUSC+X)", "Barrato")]
    [InlineData("Rimuovi formattazione (CTRL+barra spaziatrice)", "Rimuovi formattazione")]
    [InlineData("Guida (F1)", "Guida")]
    [InlineData("Bold (Ctrl + B)", "Bold")]
    public void Clean_ShortcutInParentheses_IsRemoved(string raw, string expected)
    {
        Assert.Equal(expected, LabelCleaner.Clean(raw, UiElementKind.Button, Default));
    }

    [Theory]
    [InlineData("Testo con parentesi (importante)")]
    [InlineData("Salva (copia)")]
    [InlineData("Disco locale (C:)")]
    public void Clean_OrdinaryParentheses_AreKept(string raw)
    {
        Assert.Equal(raw, LabelCleaner.Clean(raw, UiElementKind.Button, Default));
    }

    [Fact]
    public void Clean_ShortcutInParentheses_KeptWhenSettingDisabled()
    {
        var s = new ReadingSettings { StripKeyboardShortcuts = false };
        Assert.Equal("Grassetto (CTRL+G)", LabelCleaner.Clean("Grassetto (CTRL+G)", UiElementKind.Button, s));
    }

    [Theory]
    [InlineData("Download (elemento aggiunto)", "Download")]
    [InlineData("Avvio dell'accesso rapido - Desktop (elemento aggiunto)", "Desktop")]
    [InlineData("Documenti", "Documenti")]
    public void Clean_ExplorerNavigationPaneNames(string raw, string expected)
    {
        Assert.Equal(expected, LabelCleaner.Clean(raw, UiElementKind.TreeItem, Default));
    }

    [Theory]
    [InlineData("Altro")]
    [InlineData("Alto")]
    [InlineData("Alta")]
    [InlineData("Altri")]
    [InlineData("ALT")]
    [InlineData("Super")]
    [InlineData("SUPER 95")]
    [InlineData("Controllo")]
    [InlineData("Opzione")]
    [InlineData("Comando")]
    [InlineData("Windows")]
    [InlineData("Options")]
    [InlineData("Meta")]
    [InlineData("Wine")]
    public void IsKeyboardShortcut_OrdinaryWordsAreNotShortcuts(string text)
    {
        Assert.False(LabelCleaner.IsKeyboardShortcut(text));
        Assert.Equal(text, LabelCleaner.CleanOcrLine(text, Default));
    }

    [Theory]
    [InlineData("Alt-F4")]
    [InlineData("Ctrl+Alt+Canc")]
    [InlineData("Win+Shift+S")]
    [InlineData("⌘⇧N")]
    public void IsKeyboardShortcut_RealShortcuts(string text)
    {
        Assert.True(LabelCleaner.IsKeyboardShortcut(text));
    }

    [Fact]
    public void CleanOcrLine_WordAfterLabelIsNotTakenForShortcut()
    {
        Assert.Equal("Allineamento Alto", LabelCleaner.CleanOcrLine("Allineamento Alto", Default));
        Assert.Equal("Grassetto", LabelCleaner.CleanOcrLine("Grassetto (CTRL+G)", Default));
    }
}
