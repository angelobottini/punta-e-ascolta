using PuntaEAscolta.Logic.Reading;
using PuntaEAscolta.Windows.Automation;
using Xunit;

namespace PuntaEAscolta.Windows.Tests;

/// <summary>Secondo giro della revisione: tempi massimi di UIA coerenti fra loro.</summary>
public class UiaTimeoutBudgetTests
{
    [Fact]
    public void Watchdog_StaysBelowTheResolverStageTimeout()
    {
        // Una fase scaduta nel risolutore deve trovare il thread UIA già liberato dal cane da guardia.
        Assert.True(UiaTextSource.WatchdogMs < TextResolver.DefaultUiaTimeoutMs,
            $"cane da guardia {UiaTextSource.WatchdogMs} ms, fase del risolutore {TextResolver.DefaultUiaTimeoutMs} ms");
    }

    [Fact]
    public void TransactionTimeoutPlusMargin_StaysBelowTheWatchdog()
    {
        // Una chiamata UIA bloccata deve scadere da sola prima che il cane da guardia butti il thread.
        Assert.True(UiaSession.TransactionTimeoutMs + UiaTextSource.WatchdogMarginMs < UiaTextSource.WatchdogMs,
            $"transazione {UiaSession.TransactionTimeoutMs} ms + {UiaTextSource.WatchdogMarginMs} ms, cane da guardia {UiaTextSource.WatchdogMs} ms");
        Assert.True(UiaSession.ConnectionTimeoutMs <= UiaSession.TransactionTimeoutMs);
    }
}

/// <summary>Secondo giro della revisione: una finestra di WPF non è un suggerimento solo per il nome di classe.</summary>
public class TooltipWindowStyleTests
{
    private const long Affinity = 0x080800A8;           // NOACTIVATE | LAYERED | TOOLWINDOW | TRANSPARENT | TOPMOST
    private const long ClickablePopup = 0x08000088;     // NOACTIVATE | TOOLWINDOW | TOPMOST: tendina o menu che si clicca
    private const long PlainWindow = 0x00000100;        // WS_EX_WINDOWEDGE

    [Theory]
    [InlineData("tooltips_class32", PlainWindow, true)]
    [InlineData("Xaml_WindowedPopupClass", PlainWindow, true)]
    [InlineData("HwndWrapper[Affinity;;a1b2]", Affinity, true)]
    [InlineData("HwndWrapper[Affinity;;a1b2]", ClickablePopup, false)]
    [InlineData("HwndWrapper[Affinity;;a1b2]", PlainWindow, false)]
    [InlineData("Chrome_WidgetWin_1", Affinity, true)]
    [InlineData("Chrome_WidgetWin_1", ClickablePopup, false)]
    public void IsConfidentTooltip_NeedsKnownClassOrTransparentToolWindow(string className, long exStyle, bool expected)
    {
        Assert.Equal(expected, ElementReader.IsConfidentTooltip(className, exStyle));
    }

    [Fact]
    public void WpfPopupClass_IsOnlyACandidate()
    {
        Assert.True(ElementReader.IsWpfPopupClass("HwndWrapper[Affinity;;a1b2]"));
        Assert.False(ElementReader.IsKnownTooltipClass("HwndWrapper[Affinity;;a1b2]"));
        Assert.True(ElementReader.IsKnownTooltipClass("tooltips_class32"));
    }
}
