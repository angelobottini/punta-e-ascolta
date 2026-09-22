using System.Diagnostics;
using System.Text;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Reading;
using PuntaEAscolta.Logic.Resolution;
using PuntaEAscolta.Logic.Tests.Fakes;
using Xunit;

namespace PuntaEAscolta.Logic.Tests.Reading;

public class TextResolverTests
{
    // Puntatore di riferimento: con scala 1 la zona è (550, 320, 900, 360) e il puntatore nell'immagine è (450, 180).
    private static readonly ScreenPoint P = new(1000, 500);

    private sealed class Rig
    {
        public readonly CallLog Calls = new();
        public readonly FakeUi Ui;
        public readonly FakeCapture Capture;
        public readonly FakePointOcr Primary;
        public readonly FakeOcr Secondary;
        public readonly FakeClipboard Clipboard;
        public readonly FakeSettingsStore Settings = new();
        public readonly ListLog Log = new();
        public readonly TextResolver Resolver;

        public Rig(bool withClipboard = true)
        {
            Ui = new FakeUi(Calls);
            Capture = new FakeCapture(Calls);
            Primary = new FakePointOcr("win", Calls);
            Secondary = new FakeOcr("onnx", Calls);
            Clipboard = new FakeClipboard(Calls);
            Resolver = new TextResolver(Ui, Capture, Primary, Secondary, withClipboard ? Clipboard : null, Settings, Log);
        }

        public AppSettings S => Settings.Current;

        public Task<ReadOutcome> Resolve(ReadRequestKind kind = ReadRequestKind.AtPointer, CancellationToken ct = default) =>
            Resolver.ResolveAsync(new ReadRequest(kind, P), ct);
    }

    private static Task<T?> Result<T>(T? value) where T : class => Task.FromResult(value);

    private static OcrLine AtPointer(string text) => Ocr.Line(text, 420, 172);

    private static OcrLine FarAway(string text) => Ocr.Line(text, 20, 10);

    // ---------------------------------------------------------------------------------------------
    // Ordine delle fasi
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SelectionUnderPointer_WinsAndSkipsTheRest()
    {
        var rig = new Rig();
        rig.Ui.Selection = _ => Result(new UiSelectionInfo("Testo scelto.", new[] { new ScreenRect(900, 480, 300, 40) }));
        rig.Primary.Returns(AtPointer("Salva"));

        var outcome = await rig.Resolve();

        Assert.Equal(ReadSource.Selection, outcome.Source);
        Assert.Equal("Testo scelto.", outcome.Text);
        Assert.Equal(SpeechKind.Sentence, outcome.SpeechKind);
        Assert.True(outcome.Sensitive);
        Assert.Equal(new[] { "selezione" }, rig.Calls.Snapshot());
    }

    [Fact]
    public async Task SelectionElsewhere_IsIgnored_ElementNameUsed()
    {
        var rig = new Rig();
        rig.Ui.Selection = _ => Result(new UiSelectionInfo("Altro testo", new[] { new ScreenRect(0, 0, 100, 20) }));
        rig.Ui.Element = (_, _) => Result(new UiElementInfo { Kind = UiElementKind.Button, Name = "Salva", Bounds = new ScreenRect(980, 490, 60, 24), ProcessName = "WINWORD" });

        var outcome = await rig.Resolve();

        Assert.Equal(ReadSource.UiaName, outcome.Source);
        Assert.Equal("Salva", outcome.Text);
        Assert.Equal(SpeechKind.Label, outcome.SpeechKind);
        Assert.Equal(new[] { "selezione", "elemento" }, rig.Calls.Snapshot());
    }

    [Fact]
    public async Task SelectionDisabledInSettings_IsNotQueried()
    {
        var rig = new Rig();
        rig.S.Reading.ReadSelectionWhenPointerInside = false;
        rig.Ui.Element = (_, _) => Result(new UiElementInfo { Kind = UiElementKind.Button, Name = "Salva", Bounds = new ScreenRect(980, 490, 60, 24) });

        var outcome = await rig.Resolve();

        Assert.Equal("Salva", outcome.Text);
        Assert.False(rig.Calls.Contains("selezione"));
    }

    [Fact]
    public async Task ElementWithoutText_TooltipTextWins_NoOcr()
    {
        var rig = new Rig();
        rig.Ui.Element = (_, _) => Result(new UiElementInfo { Kind = UiElementKind.Pane, Bounds = new ScreenRect(0, 0, 1920, 1000) });
        rig.Ui.Tooltip = (_, _) => Result(new UiTooltipInfo("Pennello", new ScreenRect(1010, 530, 120, 24)));
        rig.Primary.Returns(AtPointer("Testo della tela"));

        var outcome = await rig.Resolve();

        Assert.Equal(ReadSource.Tooltip, outcome.Source);
        Assert.Equal("Pennello", outcome.Text);
        Assert.Equal(SpeechKind.Label, outcome.SpeechKind);
        Assert.False(rig.Calls.Contains("cattura"));
        Assert.False(rig.Calls.Contains("win"));
    }

    [Fact]
    public async Task TooltipWithoutText_OcrOfBoundsInflatedByEightPixels()
    {
        var rig = new Rig();
        rig.Ui.Tooltip = (_, _) => Result(new UiTooltipInfo(null, new ScreenRect(1010, 530, 120, 24)));
        // Immagine 136x40: il centro (68, 20) cade sulla parola.
        rig.Primary.Returns(Ocr.Line("Riempimento", 20, 12));

        var outcome = await rig.Resolve();

        Assert.Equal(ReadSource.TooltipOcr, outcome.Source);
        Assert.Equal("Riempimento", outcome.Text);
        Assert.Equal(SpeechKind.Label, outcome.SpeechKind);
        Assert.Equal(new ScreenRect(1002, 522, 136, 40), rig.Capture.Requests.Single());
    }

    [Fact]
    public async Task TooltipTextFarFromPointer_IsIgnored_ZoneOcrInstead()
    {
        var rig = new Rig();
        // Tendina di WPF (Affinity) 200 px sotto il puntatore: non è il suggerimento del punto.
        rig.Ui.Tooltip = (_, _) => Result(new UiTooltipInfo("Voce della tendina", new ScreenRect(900, 700, 200, 24)));
        rig.Primary.Returns(AtPointer("Livelli"));

        var outcome = await rig.Resolve();

        Assert.Equal(ReadSource.OcrLine, outcome.Source);
        Assert.Equal("Livelli", outcome.Text);
        Assert.Contains("suggerimento:lontano", outcome.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooltipWithoutTextFarFromPointer_ThePopupIsNotOcred()
    {
        var rig = new Rig();
        // Popup senza testo sopra e a destra del puntatore: niente OCR del popup, solo quello della zona.
        rig.Ui.Tooltip = (_, _) => Result(new UiTooltipInfo(null, new ScreenRect(1100, 300, 300, 100)));
        rig.Primary.Returns(AtPointer("Livelli"));

        var outcome = await rig.Resolve();

        Assert.Equal("Livelli", outcome.Text);
        Assert.Equal(new ScreenRect(550, 320, 900, 360), rig.Capture.Requests.Single());
        Assert.Contains("suggerimento:lontano", outcome.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooltipBelowPointer_AtHighDpi_IsStillAccepted()
    {
        var rig = new Rig();
        rig.Capture.Dpi = 1.25;
        // 120 px sotto il puntatore: oltre i 100 px al 100%, dentro i 125 px al 125%.
        rig.Ui.Tooltip = (_, _) => Result(new UiTooltipInfo("Pennello", new ScreenRect(1010, 620, 120, 30)));

        var outcome = await rig.Resolve();

        Assert.Equal(ReadSource.Tooltip, outcome.Source);
        Assert.Equal("Pennello", outcome.Text);
    }

    [Theory]
    [InlineData(1010, 530, 120, 24, 1.0, true)]    // classico: sotto e a destra del puntatore
    [InlineData(1000, 490, 120, 24, 1.0, true)]    // 10 px sopra: ancora ammesso
    [InlineData(1000, 489, 120, 24, 1.0, false)]   // 11 px sopra
    [InlineData(1000, 600, 120, 24, 1.0, true)]    // 100 px sotto
    [InlineData(1000, 601, 120, 24, 1.0, false)]   // 101 px sotto
    [InlineData(1000, 620, 120, 24, 1.25, true)]   // 120 px sotto al 125%
    [InlineData(1060, 520, 120, 24, 1.0, true)]    // bordo sinistro 60 px a destra
    [InlineData(1061, 520, 120, 24, 1.0, false)]   // 61 px a destra
    [InlineData(600, 520, 100, 24, 1.0, true)]     // bordo destro 300 px a sinistra
    [InlineData(599, 520, 100, 24, 1.0, false)]    // 301 px a sinistra
    [InlineData(1010, 530, 0, 0, 1.0, false)]      // rettangolo vuoto
    public void IsPlacedLikeTooltip_Geometry(int x, int y, int w, int h, double dpi, bool expected)
    {
        Assert.Equal(expected, TextResolver.IsPlacedLikeTooltip(new ScreenRect(x, y, w, h), P, dpi));
    }

    [Fact]
    public async Task NothingFromAccessibility_ZoneOcrScaledByDpiAndCentredOnPointer()
    {
        var rig = new Rig();
        rig.Capture.Dpi = 1.25;
        // Zona 1125x450 centrata sul puntatore: il puntatore nell'immagine è (562, 225).
        rig.Primary.Returns(Ocr.Line("Livelli", 540, 217), FarAway("Altro testo"));

        var outcome = await rig.Resolve();

        Assert.Equal(new ScreenRect(438, 275, 1125, 450), rig.Capture.Requests.Single());
        Assert.Equal(ReadSource.OcrLine, outcome.Source);
        Assert.Equal("Livelli", outcome.Text);
        Assert.Equal(SpeechKind.Label, outcome.SpeechKind);
        Assert.False(outcome.Sensitive);
        Assert.Equal(new[] { "selezione", "elemento", "suggerimento", "cattura", "win" }, rig.Calls.Snapshot());
    }

    [Fact]
    public async Task PointerIsTranslatedToImageSpace_WhenCaptureIsClippedByTheMonitor()
    {
        var rig = new Rig();
        rig.Capture.Monitor = new ScreenRect(700, 0, 1920, 1200);
        // Cattura effettiva (700, 320, 750, 360): il puntatore nell'immagine è (300, 180), non (450, 180).
        rig.Primary.Returns(Ocr.Line("Esporta", 280, 172), Ocr.Line("Importa", 440, 172));

        var outcome = await rig.Resolve();

        Assert.Equal("Esporta", outcome.Text);
    }

    // ---------------------------------------------------------------------------------------------
    // Rifinitura, BUG A delle prove dal vivo: l'OCR resta nella finestra sotto il puntatore
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ZoneOcr_StaysInsideTheWindowUnderThePointer()
    {
        var rig = new Rig();
        // Word in finestra da x=900: la zona (550, 320, 900, 360) usciva a sinistra sulla finestra che sta dietro.
        rig.Resolver.WindowBoundsAtPoint = _ => new ScreenRect(900, 40, 1400, 1000);
        // Immagine (900, 320, 550, 360), puntatore a (100, 180). La riga tocca il bordo sinistro dell'immagine, che è la fine
        // della finestra e non un taglio della zona: "Salva" non va scartata come frammento.
        rig.Primary.Returns(Ocr.Line("Salva documento", 0, 172));

        var outcome = await rig.Resolve();

        Assert.Equal(new ScreenRect(900, 320, 550, 360), rig.Capture.Requests.Single());
        Assert.Equal("Salva documento", outcome.Text);
        Assert.Contains("finestra=550x360", outcome.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZoneOcr_WindowUnknownOrFailing_UsesTheWholeZone()
    {
        var rig = new Rig();
        rig.Resolver.WindowBoundsAtPoint = _ => throw new InvalidOperationException("finestra sparita");
        rig.Primary.Returns(AtPointer("Salva"));

        var outcome = await rig.Resolve();

        Assert.Equal(new ScreenRect(550, 320, 900, 360), rig.Capture.Requests.Single());
        Assert.Equal("Salva", outcome.Text);
    }

    [Fact]
    public async Task ZoneAroundPointer_IsAlsoLimitedToTheWindow()
    {
        var rig = new Rig();
        rig.Resolver.WindowBoundsAtPoint = _ => new ScreenRect(700, 400, 800, 200);
        rig.Primary.Returns(Ocr.Line("Titolo", 250, 50));

        await rig.Resolve(ReadRequestKind.ZoneAroundPointer);

        Assert.Equal(new ScreenRect(700, 400, 750, 200), rig.Capture.Requests.Single());
    }

    [Theory]
    [InlineData(900, 40, 1400, 1000, 900, 320, 550, 360)]    // finestra a destra: tolto il lato sinistro
    [InlineData(0, 0, 1920, 1200, 550, 320, 900, 360)]       // finestra più grande della zona: zona intera
    [InlineData(0, 0, 400, 300, 550, 320, 900, 360)]         // finestra che non contiene il puntatore (bordo invisibile): zona intera
    [InlineData(990, 490, 30, 20, 990, 490, 30, 20)]         // finestra minuscola sotto il puntatore: solo lei
    [InlineData(0, 0, 0, 0, 550, 320, 900, 360)]             // rettangolo vuoto: zona intera
    public void ClipZoneToWindow_Geometry(int wx, int wy, int ww, int wh, int ex, int ey, int ew, int eh)
    {
        var zone = new ScreenRect(550, 320, 900, 360);
        Assert.Equal(new ScreenRect(ex, ey, ew, eh), TextResolver.ClipZoneToWindow(zone, P, new ScreenRect(wx, wy, ww, wh)));
        Assert.Equal(zone, TextResolver.ClipZoneToWindow(zone, P, null));
    }

    // ---------------------------------------------------------------------------------------------
    // Motori OCR
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PointPass_InvokedWhenNothingNearPointer_AndItsTextIsUsed()
    {
        var rig = new Rig();
        rig.Primary.Returns(FarAway("Titolo lontano"));
        rig.Primary.PointReturns(AtPointer("Esporta"));

        var outcome = await rig.Resolve();

        Assert.Equal("Esporta", outcome.Text);
        Assert.Equal((450.0, 180.0), rig.Primary.Points.Single());
        Assert.Equal(1, rig.Calls.Count("win:mirato"));
        Assert.False(rig.Calls.Contains("onnx"));
    }

    [Fact]
    public async Task PointPass_NotInvokedWhenPrimaryFindsTextNearPointer()
    {
        var rig = new Rig();
        rig.Primary.Returns(AtPointer("Salva"));
        rig.Primary.PointReturns(AtPointer("NON USARE"));

        var outcome = await rig.Resolve();

        Assert.Equal("Salva", outcome.Text);
        Assert.Empty(rig.Primary.Points);
        Assert.False(rig.Calls.Contains("onnx"));
    }

    [Fact]
    public async Task Auto_SecondaryUsedWhenPrimaryAndPointPassFindNothing()
    {
        var rig = new Rig();
        rig.Primary.Returns(FarAway("Titolo lontano"));
        rig.Secondary.Returns(AtPointer("Maglietta"));

        var outcome = await rig.Resolve();

        Assert.Equal("Maglietta", outcome.Text);
        var ocrCalls = rig.Calls.Snapshot().Where(c => c.StartsWith("win", StringComparison.Ordinal) || c == "onnx").ToList();
        Assert.Equal(new[] { "win", "win:mirato", "onnx" }, ocrCalls);
    }

    [Fact]
    public async Task Auto_ImageElementWithFewPrimaryLines_UsesSecondary()
    {
        var rig = new Rig();
        rig.Ui.Element = (_, _) => Result(new UiElementInfo { Kind = UiElementKind.Image, Bounds = new ScreenRect(0, 0, 1920, 1080) });
        rig.Primary.Returns(AtPointer("MAGL1ETTA"));
        rig.Secondary.Returns(AtPointer("MAGLIETTA ROSSA"));

        var outcome = await rig.Resolve();

        Assert.Equal("MAGLIETTA ROSSA", outcome.Text);
        Assert.Empty(rig.Primary.Points);
    }

    [Fact]
    public async Task Auto_ImageElementWithManyPrimaryLines_DoesNotUseSecondary()
    {
        var rig = new Rig();
        rig.Ui.Element = (_, _) => Result(new UiElementInfo { Kind = UiElementKind.Image, Bounds = new ScreenRect(0, 0, 1920, 1080) });
        rig.Primary.Returns(AtPointer("Cartello"), FarAway("Riga uno"), Ocr.Line("Riga due", 20, 320));

        var outcome = await rig.Resolve();

        Assert.Equal("Cartello", outcome.Text);
        Assert.False(rig.Calls.Contains("onnx"));
    }

    [Fact]
    public async Task WindowsOnly_NeverUsesSecondary()
    {
        var rig = new Rig();
        rig.S.Ocr.Mode = OcrMode.WindowsOnly;
        rig.Primary.Returns(FarAway("Titolo lontano"));
        rig.Secondary.Returns(AtPointer("Maglietta"));

        var outcome = await rig.Resolve();

        Assert.False(outcome.HasText);
        Assert.Equal(ReadSource.None, outcome.Source);
        Assert.False(rig.Calls.Contains("onnx"));
        Assert.True(rig.Calls.Contains("win:mirato"));
    }

    [Fact]
    public async Task OnnxOnly_UsesOnlySecondary()
    {
        var rig = new Rig();
        rig.S.Ocr.Mode = OcrMode.OnnxOnly;
        rig.Primary.Returns(AtPointer("Primario"));
        rig.Secondary.Returns(AtPointer("Onnx"));

        var outcome = await rig.Resolve();

        Assert.Equal("Onnx", outcome.Text);
        Assert.False(rig.Calls.Contains("win"));
    }

    [Fact]
    public async Task OnnxOnly_FallsBackToPrimaryWhenOnnxUnavailable()
    {
        var rig = new Rig();
        rig.S.Ocr.Mode = OcrMode.OnnxOnly;
        rig.Secondary.IsAvailable = false;
        rig.Primary.Returns(AtPointer("Primario"));

        var outcome = await rig.Resolve();

        Assert.Equal("Primario", outcome.Text);
        Assert.False(rig.Calls.Contains("onnx"));
    }

    [Fact]
    public async Task PrimaryThrows_SecondaryIsUsed()
    {
        var rig = new Rig();
        rig.Primary.Handler = (_, _) => throw new InvalidOperationException("motore guasto");
        rig.Secondary.Returns(AtPointer("Salva"));

        var outcome = await rig.Resolve();

        Assert.Equal("Salva", outcome.Text);
        Assert.Contains("ocr:win:errore", outcome.Diagnostics);
        Assert.Empty(rig.Primary.Points);
    }

    [Fact]
    public async Task SmallElementWithoutText_ClipsOcrToElementBounds()
    {
        var rig = new Rig();
        rig.Ui.Element = (_, _) => Result(new UiElementInfo { Kind = UiElementKind.Button, Bounds = new ScreenRect(990, 490, 30, 24) });
        rig.Resolver.ResolveUiElement = (_, _) => null;
        var clips = new List<ImageRect?>();
        rig.Resolver.SelectOcrText = (r, x, y, s, whole, clip) =>
        {
            lock (clips) clips.Add(clip);
            return PointerTextSelector.Select(r, x, y, s, whole, clip);
        };
        // Etichetta sulla stessa riga ma fuori dal pulsante: con il ritaglio non va letta.
        rig.Primary.Returns(Ocr.Line("Etichetta vicina", 480, 172));

        var outcome = await rig.Resolve();

        Assert.False(outcome.HasText);
        Assert.NotEmpty(clips);
        Assert.All(clips, c => Assert.Equal(new ImageRect(440, 170, 30, 24), c));
    }

    [Fact]
    public async Task LargeElementWithoutText_DoesNotClip()
    {
        var rig = new Rig();
        rig.Ui.Element = (_, _) => Result(new UiElementInfo { Kind = UiElementKind.Custom, Bounds = new ScreenRect(0, 0, 1920, 1080) });
        rig.Resolver.ResolveUiElement = (_, _) => null;
        ImageRect? seen = new ImageRect(1, 1, 1, 1);
        rig.Resolver.SelectOcrText = (r, x, y, s, whole, clip) =>
        {
            seen = clip;
            return PointerTextSelector.Select(r, x, y, s, whole, clip);
        };
        rig.Primary.Returns(AtPointer("Livello 1"), FarAway("Riga"), Ocr.Line("Altra", 20, 320));

        var outcome = await rig.Resolve();

        Assert.Equal("Livello 1", outcome.Text);
        Assert.Null(seen);
    }

    [Fact]
    public async Task OcrOnlyProcess_SkipsAccessibilityDecision()
    {
        var rig = new Rig();
        rig.S.Reading.OcrOnlyProcesses.Add("Photo.exe");
        rig.Ui.Element = (_, _) => Result(new UiElementInfo { Kind = UiElementKind.Button, Name = "NomeInterno", ProcessName = "photo", Bounds = new ScreenRect(980, 490, 60, 24) });
        int resolverCalls = 0;
        rig.Resolver.ResolveUiElement = (i, s) => { resolverCalls++; return UiTextResolver.Resolve(i, s); };
        rig.Primary.Returns(AtPointer("Pennello"));

        var outcome = await rig.Resolve();

        Assert.Equal("Pennello", outcome.Text);
        Assert.Equal(0, resolverCalls);
    }

    // ---------------------------------------------------------------------------------------------
    // Tempi massimi e annullamento
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SlowElementStage_IgnoringCancellation_TimesOutAndFallsBackToOcr()
    {
        var rig = new Rig();
        rig.Resolver.UiaTimeout = TimeSpan.FromMilliseconds(150);
        var never = new TaskCompletionSource<UiElementInfo?>();
        rig.Ui.Element = (_, _) => never.Task;
        rig.Primary.Returns(AtPointer("Salva"));
        var sw = Stopwatch.StartNew();

        var outcome = await rig.Resolve();

        Assert.Equal("Salva", outcome.Text);
        Assert.Contains("elemento:tempo-scaduto", outcome.Diagnostics);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"troppo lento: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task SlowPrimaryOcr_HonoringCancellation_TimesOut_SecondaryUsed_NoPointPass()
    {
        var rig = new Rig();
        rig.Resolver.OcrTimeout = TimeSpan.FromMilliseconds(150);
        CancellationToken seen = default;
        rig.Primary.Handler = async (_, ct) =>
        {
            seen = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return OcrResult.Empty("win");
        };
        rig.Secondary.Returns(AtPointer("Salva"));

        var outcome = await rig.Resolve();

        Assert.Equal("Salva", outcome.Text);
        Assert.True(seen.IsCancellationRequested);
        Assert.Contains("ocr:win:tempo-scaduto", outcome.Diagnostics);
        Assert.Empty(rig.Primary.Points);
    }

    [Fact]
    public async Task RequestCancellation_IsPropagatedPromptly()
    {
        var rig = new Rig();
        rig.Resolver.OcrTimeout = TimeSpan.FromSeconds(30);
        rig.Primary.Handler = (_, _) => new TaskCompletionSource<OcrResult>().Task; // non onora l'annullamento
        using var cts = new CancellationTokenSource(150);
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Resolve(ct: cts.Token));

        Assert.True(sw.ElapsedMilliseconds < 3000, $"troppo lento: {sw.ElapsedMilliseconds} ms");
        Assert.False(rig.Calls.Contains("onnx"));
    }

    /// <summary>
    /// Rifinitura (prove dal vivo, osservazione 1): annullata la lettura, anche la fase in corso (l'OCR ONNX) deve ricevere
    /// l'annullamento. Prima la continuazione eseguita dentro Cancel chiudeva il token della fase prima che fosse annullato
    /// e il motore lavorava fino in fondo.
    /// </summary>
    [Fact]
    public async Task RequestCancellation_AlsoCancelsTheRunningStageToken()
    {
        var rig = new Rig();
        rig.Resolver.OcrTimeout = TimeSpan.FromSeconds(30);
        var stageToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Primary.Handler = (_, ct) =>
        {
            stageToken.TrySetResult(ct);
            return new TaskCompletionSource<OcrResult>().Task; // lavora "a lungo" e controlla il token solo fra le fasi
        };
        using var cts = new CancellationTokenSource();
        var resolving = rig.Resolve(ct: cts.Token);
        var token = await stageToken.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Come nell'app: Cancel dal thread del worker, senza contesto di sincronizzazione, dove le continuazioni girano
        // dentro Cancel (il thread del test di xUnit ha un contesto e le rimanderebbe, nascondendo il difetto).
        await Task.Run(cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving);
        Assert.True(token.IsCancellationRequested, "il token della fase OCR non è stato annullato");
    }

    // ---------------------------------------------------------------------------------------------
    // Selezione e zona
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SelectionRequest_ReadsAnySelection()
    {
        var rig = new Rig();
        rig.Ui.Selection = _ => Result(new UiSelectionInfo("Selezione lontana.", new[] { new ScreenRect(0, 0, 10, 10) }));

        var outcome = await rig.Resolve(ReadRequestKind.Selection);

        Assert.Equal(ReadSource.Selection, outcome.Source);
        Assert.Equal("Selezione lontana.", outcome.Text);
        Assert.False(rig.Calls.Contains("appunti"));
    }

    [Fact]
    public async Task SelectionRequest_FallsBackToClipboard()
    {
        var rig = new Rig();
        rig.Clipboard.Text = "Testo copiato dagli appunti.";

        var outcome = await rig.Resolve(ReadRequestKind.Selection);

        Assert.Equal(ReadSource.ClipboardSelection, outcome.Source);
        Assert.Equal("Testo copiato dagli appunti.", outcome.Text);
        Assert.True(outcome.Sensitive);
        Assert.Equal(new[] { "selezione", "appunti" }, rig.Calls.Snapshot());
    }

    [Fact]
    public async Task SelectionRequest_WithoutClipboardReader_IsNothing()
    {
        var rig = new Rig(withClipboard: false);

        var outcome = await rig.Resolve(ReadRequestKind.Selection);

        Assert.False(outcome.HasText);
        Assert.Contains("appunti:assenti", outcome.Diagnostics);
    }

    [Fact]
    public async Task ZoneAroundPointer_ReadsWholeZoneWithoutAccessibility()
    {
        var rig = new Rig();
        rig.Primary.Returns(Ocr.Line("Primo cartello", 300, 60), Ocr.Line("Secondo cartello", 300, 250));

        var outcome = await rig.Resolve(ReadRequestKind.ZoneAroundPointer);

        Assert.Equal(ReadSource.OcrZone, outcome.Source);
        Assert.Contains("Primo cartello", outcome.Text);
        Assert.Contains("Secondo cartello", outcome.Text);
        Assert.False(rig.Calls.Contains("elemento"));
        Assert.False(rig.Calls.Contains("selezione"));
    }

    [Fact]
    public async Task ZoneAroundPointer_DropsWordsCutByTheCaptureBorder()
    {
        var rig = new Rig();
        // "mento" è il resto di "Documento" tagliato dal bordo sinistro della zona.
        rig.Primary.Returns(Ocr.Line("mento aperto", 0, 60), Ocr.Line("Titolo", 400, 172));

        var outcome = await rig.Resolve(ReadRequestKind.ZoneAroundPointer);

        Assert.DoesNotContain("mento", outcome.Text);
        Assert.Contains("aperto", outcome.Text);
        Assert.Contains("Titolo", outcome.Text);
        Assert.Contains("tagliati=", outcome.Diagnostics);
    }

    // ---------------------------------------------------------------------------------------------
    // Rifinitura
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task LongText_IsTruncatedAtWordBoundary()
    {
        var rig = new Rig();
        rig.S.Reading.MaxCharsPerRead = 40;
        var text = string.Join(' ', Enumerable.Repeat("parola", 30));
        rig.Ui.Selection = _ => Result(new UiSelectionInfo(text, new[] { new ScreenRect(900, 480, 300, 40) }));

        var outcome = await rig.Resolve();

        Assert.True(outcome.Text.Length <= 40);
        Assert.StartsWith(outcome.Text, text);
        Assert.Equal(' ', text[outcome.Text.Length]);
        Assert.Contains("troncato=", outcome.Diagnostics);
    }

    [Fact]
    public async Task LabelLanguageSetting_AppliesToLabels()
    {
        var rig = new Rig();
        rig.S.Speech.LabelLanguage = LabelLanguageMode.English;
        rig.Ui.Element = (_, _) => Result(new UiElementInfo { Kind = UiElementKind.Button, Name = "Salva", Bounds = new ScreenRect(980, 490, 60, 24) });

        var outcome = await rig.Resolve();

        Assert.Equal(SpeechKind.Label, outcome.SpeechKind);
        Assert.Equal("en", outcome.LanguageHint);
    }

    [Fact]
    public async Task LabelLanguageSetting_DoesNotForceSentences()
    {
        var rig = new Rig();
        rig.S.Speech.LabelLanguage = LabelLanguageMode.English;
        rig.Ui.Selection = _ => Result(new UiSelectionInfo("Questa è una frase del documento che sto leggendo.", new[] { new ScreenRect(900, 480, 300, 40) }));

        var outcome = await rig.Resolve();

        Assert.Equal("it", outcome.LanguageHint);
    }

    [Fact]
    public async Task OnlyEmoji_IsNothing()
    {
        var rig = new Rig();
        var emoji = char.ConvertFromUtf32(0x1F600) + " " + char.ConvertFromUtf32(0x1F44D);
        rig.Ui.Selection = _ => Result(new UiSelectionInfo(emoji, new[] { new ScreenRect(900, 480, 300, 40) }));

        var outcome = await rig.Resolve();

        Assert.False(outcome.HasText);
        Assert.Contains("solo-emoji", outcome.Diagnostics);
    }

    [Fact]
    public async Task Diagnostics_DescribePathAndTimings()
    {
        var rig = new Rig();
        rig.Primary.Returns(AtPointer("Salva"));

        var outcome = await rig.Resolve();

        Assert.Contains("AtPointer@1000,500", outcome.Diagnostics);
        Assert.Contains("elemento:no(", outcome.Diagnostics);
        Assert.Contains("zona=900x360", outcome.Diagnostics);
        Assert.Contains("ocr:win:ok(", outcome.Diagnostics);
        Assert.Contains("-> OcrLine", outcome.Diagnostics);
        Assert.Contains("totale=", outcome.Diagnostics);
    }

    [Fact]
    public async Task SpokenText_IsLoggedOnlyAtDebugLevel()
    {
        var rig = new Rig();
        rig.Primary.Returns(AtPointer("Segreto"));

        await rig.Resolve();
        Assert.DoesNotContain(rig.Log.Entries, e => e.Message.Contains("Segreto", StringComparison.Ordinal));

        rig.Log.IsDebugEnabled = true;
        await rig.Resolve();
        Assert.Contains(rig.Log.Entries, e => e.Level == "DEBUG" && e.Message.Contains("Segreto", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // Funzioni interne
    // ---------------------------------------------------------------------------------------------

    private static CapturedImage Image(ScreenRect bounds) => new(new byte[4], bounds.Width, bounds.Height, bounds, 1.0);

    [Fact]
    public void DropCutText_RemovesWordsTouchingACutSide()
    {
        var desired = new ScreenRect(550, 320, 900, 360);
        var result = new OcrResult(new[] { Ocr.Line("mento prova", 0, 100) }, "win", 1);

        var filtered = TextResolver.DropCutText(result, Image(desired), desired, 450, 180);

        var line = Assert.Single(filtered.Lines);
        Assert.Equal("prova", line.Text);
        Assert.Equal(46, line.Box.X);
    }

    [Fact]
    public void DropCutText_KeepsTextAtAMonitorEdge()
    {
        var desired = new ScreenRect(-100, 320, 900, 360);
        var actual = new ScreenRect(0, 320, 800, 360); // ritagliata dal bordo sinistro del monitor
        var result = new OcrResult(new[] { Ocr.Line("File Modifica", 0, 100) }, "win", 1);

        var filtered = TextResolver.DropCutText(result, Image(actual), desired, 350, 180);

        Assert.Same(result, filtered);
    }

    [Fact]
    public void DropCutText_RemovesLinesCutAtTopOrBottom_ButNotThePointerLine()
    {
        var desired = new ScreenRect(550, 320, 900, 360);
        var result = new OcrResult(new[]
        {
            Ocr.Line("Riga tagliata sopra", 300, 0),
            Ocr.Line("Riga del puntatore", 400, 172),
            Ocr.Line("Riga tagliata sotto", 300, 350),
        }, "win", 1);

        var filtered = TextResolver.DropCutText(result, Image(desired), desired, 450, 180);

        Assert.Equal(new[] { "Riga del puntatore" }, filtered.Lines.Select(l => l.Text));
    }

    [Fact]
    public void ZoneAround_ScalesAndCentres()
    {
        var zone = TextResolver.ZoneAround(new ScreenPoint(100, 100), new OcrSettings { ZoneWidth = 900, ZoneHeight = 360 }, 1.5);
        Assert.Equal(new ScreenRect(-575, -170, 1350, 540), zone);
    }

    [Fact]
    public void NormalizeWhitespace_HandlesWordControlCharacters()
    {
        var sb = new StringBuilder();
        sb.Append("Ciao").Append((char)7).Append("mondo").Append((char)0x2029).Append("Fine").Append((char)0xFFFC).Append((char)0x00A0).Append("  qui");

        Assert.Equal("Ciao mondo\nFine qui", TextResolver.NormalizeWhitespace(sb.ToString()));
    }

    [Theory]
    [InlineData(299, 119, 1.0, true)]
    [InlineData(300, 100, 1.0, false)]
    [InlineData(370, 140, 1.25, true)]
    public void IsSmallElement_UsesScaledThreshold(int w, int h, double dpi, bool expected)
    {
        Assert.Equal(expected, TextResolver.IsSmallElement(new ScreenRect(0, 0, w, h), dpi));
    }

    [Theory]
    [InlineData(UiElementKind.ScrollBar, UiElementKind.Pane)]
    [InlineData(UiElementKind.Unknown, UiElementKind.ScrollBar)]
    public async Task ScrollBarWithoutName_IsSilent_NoOcrOfNeighbourLine(UiElementKind kind, UiElementKind parent)
    {
        // Prova dal vivo: puntatore sulla barra di scorrimento orizzontale di Word, l'OCR leggeva la barra di stato sotto.
        var rig = new Rig();
        rig.Ui.Element = (_, _) => Result(new UiElementInfo { Kind = kind, ParentKind = parent, Bounds = new ScreenRect(300, 490, 1400, 17), ProcessName = "WINWORD" });
        rig.Primary.Returns(AtPointer("Accessibilità: conforme"));

        var outcome = await rig.Resolve();

        Assert.False(outcome.HasText);
        Assert.Contains("barra-di-scorrimento", outcome.Diagnostics);
        Assert.False(rig.Calls.Contains("win"));
        Assert.False(rig.Calls.Contains("suggerimento"));
    }
}
