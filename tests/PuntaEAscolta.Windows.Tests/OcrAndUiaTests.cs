using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Windows.Automation;
using PuntaEAscolta.Windows.Ocr;
using Xunit;
using UIA = Interop.UIAutomationClient;

namespace PuntaEAscolta.Windows.Tests;

/// <summary>Revisione del 22/09/2026: passaggio OCR mirato e coordinate di Windows OCR.</summary>
public class WindowsOcrTests
{
    private static OcrLine L(string text, double x, double y, double w, double h) =>
        new(text, new ImageRect(x, y, w, h), new[] { new OcrWord(text, new ImageRect(x, y, w, h)) });

    [Fact]
    public void DropLinesCutByCrop_RemovesLinesOnInteriorEdgesOnly()
    {
        // Fascia 0..1125 x 165..285 dentro un'immagine 1125x450: tagliano solo i bordi superiore e inferiore.
        var band = new Rectangle(0, 165, 1125, 120);
        var lines = new[]
        {
            L("va", 364, 165, 20, 10),          // tagliata dal bordo superiore
            L("Condividi", 304, 189, 70, 14),
            L("Esporta come PDF...", 0, 219, 150, 14),   // tocca il bordo sinistro, che è il bordo dell'immagine: resta
            L("Stampa", 304, 276, 50, 10),      // tagliata dal bordo inferiore
        };
        var kept = OcrPreprocessor.DropLinesCutByCrop(lines, band, 1125, 450);
        Assert.Equal(new[] { "Condividi", "Esporta come PDF..." }, kept.Select(l => l.Text));
    }

    [Fact]
    public void DropLinesCutByCrop_WholeImage_KeepsEverything()
    {
        var lines = new[] { L("Nuovo", 0, 0, 40, 12), L("Esci", 10, 438, 30, 12) };
        Assert.Same(lines, OcrPreprocessor.DropLinesCutByCrop(lines, new Rectangle(0, 0, 100, 450), 100, 450));
    }

    [Fact]
    public void UndoTextAngle_BringsDeskewedBoxesBackToTheirRow()
    {
        // Misurato su docs/research/probe/affinity-lowcontrast-normal.png (fascia 1848x288, TextAngle 3,2):
        // "FF0000" riportato dal motore a (1652, 96) 18x15 sta in realtà alla riga di y 137 circa.
        var (x, y) = WindowsOcrEngine.UndoTextAngle(1652, 96, 18, 15, 1848, 288, 3.2);
        Assert.InRange(y, 133, 141);
        Assert.InRange(x, 1648, 1660);
        Assert.Equal((10.0, 20.0), WindowsOcrEngine.UndoTextAngle(10, 20, 5, 5, 100, 100, 0));
    }

    /// <summary>
    /// Menu scuro in stile Affinity (1125x450, la zona 900x360 al 125%) con tre voci disabilitate in grigio; l'etichetta
    /// "Esporta come PDF..." comincia a sinistra del vecchio ritaglio 400x120 centrato sul puntatore (x 362).
    /// </summary>
    private static CapturedImage DarkMenu(int labelX)
    {
        const int W = 1125, H = 450;
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        bmp.SetResolution(120, 120);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 46, 46, 46));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            using var enabled = new SolidBrush(Color.FromArgb(255, 225, 225, 225));
            using var disabled = new SolidBrush(Color.FromArgb(255, 76, 76, 76));
            string[] rows = ["Nuovo...", "Apri...", "Chiudi", "Salva", "Salva con nome...", "Condividi", "Esporta come PDF...", "Esporta come immagine...", "Stampa...", "Esci"];
            for (int i = 0; i < rows.Length; i++)
            {
                int cy = 225 + (i - 6) * 30;
                var size = g.MeasureString(rows[i], font);
                g.DrawString(rows[i], font, i is 5 or 6 or 7 ? disabled : enabled, labelX, cy - size.Height / 2);
            }
        }
        var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[W * H * 4];
            for (int y = 0; y < H; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * W * 4, W * 4);
            return new CapturedImage(bytes, W, H, new ScreenRect(0, 0, W, H), 1.25);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    [Theory]
    [InlineData(300)]
    [InlineData(320)]
    [InlineData(340)]
    public async Task PointPass_DisabledMenuItem_IsReadWhole(int labelX)
    {
        using var engine = new WindowsOcrEngine(() => "it-IT", NullLog.Instance);
        if (!engine.IsAvailable) return;   // nessuna lingua OCR installata: niente da verificare su questo PC

        var result = await engine.RecognizeAroundPointAsync(DarkMenu(labelX), 562, 225, CancellationToken.None);

        var row = result.Lines.Where(l => l.Box.Y <= 232 && l.Box.Bottom >= 218).ToList();
        Assert.Single(row);
        Assert.StartsWith("Esporta come PDF", row[0].Text);
        Assert.InRange(row[0].Box.X, labelX - 4, labelX + 8);
        // Nessuna riga tagliata dai bordi superiore e inferiore della fascia (165 e 285).
        Assert.All(result.Lines, l => Assert.True(l.Box.Y > 167 && l.Box.Bottom < 283, l.Text));
    }
}

/// <summary>Revisione del 22/09/2026: la richiesta di cache UIA deve contenere le proprietà di Value e Toggle.</summary>
public class UiaCacheTests
{
    private const int BsAutoCheckBox = 0x3;
    private const int BmSetCheck = 0x00F1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [Fact]
    public void CachedValueAndToggleState_AreAvailable()
    {
        Assert.Contains(UiaIds.ValueValue, UiaIds.CachedProperties);
        Assert.Contains(UiaIds.ToggleToggleState, UiaIds.CachedProperties);

        // Finestre Win32 nascoste (mai mostrate, nessun focus), sullo stesso thread MTA che interroga UIA.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            IntPtr edit = IntPtr.Zero, check = IntPtr.Zero;
            try
            {
                edit = CreateWindowExW(0, "EDIT", "1851,75 EUR", 0, 0, 0, 200, 30, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                check = CreateWindowExW(0, "BUTTON", "Mostra griglia", BsAutoCheckBox, 0, 0, 200, 30, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, edit);
                Assert.NotEqual(IntPtr.Zero, check);
                SendMessageW(check, BmSetCheck, (IntPtr)1, IntPtr.Zero);

                var session = new UiaSession();
                UIA.IUIAutomationElement editElement = session.Automation.ElementFromHandleBuildCache(edit, session.ElementCache);
                UIA.IUIAutomationElement checkElement = session.Automation.ElementFromHandleBuildCache(check, session.ElementCache);

                var pattern = (UIA.IUIAutomationValuePattern)editElement.GetCachedPattern(UiaIds.ValuePattern);
                Assert.Equal("1851,75 EUR", pattern.CachedValue);   // senza ValueValue in cache: E_INVALIDARG
                Assert.Equal("1851,75 EUR", ElementReader.ReadValue(editElement));
                Assert.Equal(UiToggleState.On, ElementReader.ReadToggle(checkElement));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (edit != IntPtr.Zero) DestroyWindow(edit);
                if (check != IntPtr.Zero) DestroyWindow(check);
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "UIA non ha risposto");
        if (failure is not null) throw new Xunit.Sdk.XunitException("Verifica UIA fallita: " + failure);
    }
}
