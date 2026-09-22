using System.Diagnostics;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Ocr.Onnx;

namespace PuntaEAscolta.OcrBench;

/// <summary>
/// Prove di robustezza del motore: annullamento, buffer errati, immagini minuscole ed estreme, chiamate concorrenti,
/// modelli mancanti, Dispose. Uso: OcrBench --robustezza &lt;immagine.png&gt; (consigliato il popup del menu File).
/// </summary>
internal static class Robustness
{
    public static async Task<int> RunAsync(string imagePath)
    {
        var log = new ConsoleLog(debug: false);
        var image = ImageLoader.Load(imagePath, 1.0, invert: false);
        int failures = 0;

        void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK      " : "FALLITO ")} {name}: {detail}");
            if (!ok)
            {
                failures++;
            }
        }

        using (var engine = new OnnxOcrEngine(log))
        {
            await engine.WarmUpAsync().ConfigureAwait(false);

            // 1. Token già annullato.
            try
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                await engine.RecognizeAsync(image, cts.Token).ConfigureAwait(false);
                Report("token gia annullato", false, "nessuna eccezione");
            }
            catch (OperationCanceledException)
            {
                Report("token gia annullato", true, "OperationCanceledException");
            }

            // 2. Annullamento durante il lavoro, poi una richiesta normale deve funzionare.
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
                var r = await engine.RecognizeAsync(image, cts.Token).ConfigureAwait(false);
                Report("annullamento dopo 40 ms", false, $"completato senza annullare ({r.Lines.Count} righe, {sw.ElapsedMilliseconds} ms)");
            }
            catch (OperationCanceledException)
            {
                Report("annullamento dopo 40 ms", sw.ElapsedMilliseconds < 400, $"OperationCanceledException dopo {sw.ElapsedMilliseconds} ms");
            }

            var after = await engine.RecognizeAsync(image, CancellationToken.None).ConfigureAwait(false);
            Report("richiesta dopo l'annullamento", after.Lines.Count > 0, $"{after.Lines.Count} righe");

            // 3. Buffer più corto del dichiarato.
            var shortImage = image with { Bgra = new byte[16] };
            var empty = await engine.RecognizeAsync(shortImage, CancellationToken.None).ConfigureAwait(false);
            Report("buffer corto", empty.Lines.Count == 0, "risultato vuoto, nessuna eccezione");

            // 4. Immagine vuota e 1x1.
            var zero = await engine.RecognizeAsync(new CapturedImage([], 0, 0, default, 1.25), CancellationToken.None).ConfigureAwait(false);
            var one = await engine.RecognizeAsync(new CapturedImage(new byte[4], 1, 1, new ScreenRect(0, 0, 1, 1), 1.25), CancellationToken.None).ConfigureAwait(false);
            Report("immagini 0x0 e 1x1", zero.Lines.Count == 0 && one.Lines.Count == 0, "vuote");

            // 5. Ritaglio stretto su "Home" (tooltip di una riga, 110x22 dal popup del menu File).
            var home = Crop(image, 10, 4, 110, 22);
            sw.Restart();
            var homeResult = await engine.RecognizeAsync(home, CancellationToken.None).ConfigureAwait(false);
            Report("ritaglio 110x22 su Home", homeResult.Lines.Any(l => l.Text == "Home"),
                $"'{string.Join(" | ", homeResult.Lines.Select(l => l.Text))}' in {sw.ElapsedMilliseconds} ms");

            // 6. Percorso mirato con punto fuori dall'immagine e ai bordi.
            var outside = await engine.RecognizeNearAsync(image, -50, 5000, CancellationToken.None).ConfigureAwait(false);
            var corner = await engine.RecognizeNearAsync(image, 0, 0, CancellationToken.None).ConfigureAwait(false);
            Report("punto fuori e nell'angolo", true, $"fuori {outside.Lines.Count} righe, angolo {corner.Lines.Count} righe ('{corner.Lines.FirstOrDefault()?.Text}')");

            // 7. Striscia estrema 3000x40 e immagine grande 2560x1440 (ripetizione del popup).
            var strip = Tile(image, 3000, 40);
            sw.Restart();
            var stripResult = await engine.RecognizeAsync(strip, CancellationToken.None).ConfigureAwait(false);
            Report("striscia 3000x40", true, $"{stripResult.Lines.Count} righe in {sw.ElapsedMilliseconds} ms, scala {engine.LastTimings.DetectionScale:0.00}");
            var big = Tile(image, 2560, 1440);
            sw.Restart();
            var bigResult = await engine.RecognizeAsync(big, CancellationToken.None).ConfigureAwait(false);
            Report("immagine 2560x1440", bigResult.Lines.Count > 0, $"{bigResult.Lines.Count} righe in {sw.ElapsedMilliseconds} ms, scala {engine.LastTimings.DetectionScale:0.00}");

            // 8. Quattro chiamate concorrenti: serializzate dal semaforo, stesso risultato.
            sw.Restart();
            var tasks = Enumerable.Range(0, 4).Select(_ => engine.RecognizeNearAsync(image, 50, 88, CancellationToken.None)).ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            bool same = results.All(r => r.Lines.Select(l => l.Text).SequenceEqual(results[0].Lines.Select(l => l.Text)));
            Report("4 chiamate concorrenti", same && results[0].Lines.Count > 0, $"{results[0].Lines.Count} righe ciascuna, {sw.ElapsedMilliseconds} ms in tutto");

            // 9. Coordinate: ogni riga e ogni parola dentro l'immagine, parole dentro la riga.
            bool inside = after.Lines.All(l => l.Box.X >= 0 && l.Box.Y >= 0 && l.Box.Right <= image.Width && l.Box.Bottom <= image.Height
                                               && l.Words.All(w => w.Box.X >= l.Box.X - 0.5 && w.Box.Right <= l.Box.Right + 0.5));
            Report("coordinate nell'immagine", inside, $"{after.Lines.Count} righe, {after.Lines.Sum(l => l.Words.Count)} parole");
        }

        // 10. Modelli mancanti.
        using (var missing = new OnnxOcrEngine(log, Path.Combine(Path.GetTempPath(), "cartella-inesistente-ocr")))
        {
            bool before = missing.IsAvailable;
            var r = await missing.RecognizeAsync(image, CancellationToken.None).ConfigureAwait(false);
            Report("modelli mancanti", !before && r.Lines.Count == 0 && !missing.IsAvailable,
                $"IsAvailable {before}, righe {r.Lines.Count}, motivo: {missing.UnavailableReason ?? "(nessuno: file assenti, nessun caricamento tentato)"}");
        }

        // 11. Dispose durante e dopo.
        var disposable = new OnnxOcrEngine(log);
        await disposable.WarmUpAsync().ConfigureAwait(false);
        var running = disposable.RecognizeAsync(image, CancellationToken.None);
        await Task.Delay(20).ConfigureAwait(false);
        disposable.Dispose();
        try
        {
            var r = await running.ConfigureAwait(false);
            var afterDispose = await disposable.RecognizeAsync(image, CancellationToken.None).ConfigureAwait(false);
            Report("Dispose durante e dopo", afterDispose.Lines.Count == 0 && !disposable.IsAvailable, $"in corso: {r.Lines.Count} righe; dopo: vuoto");
        }
        catch (Exception ex)
        {
            Report("Dispose durante e dopo", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine(failures == 0 ? "Tutte le prove superate." : $"{failures} prove fallite.");
        return failures == 0 ? 0 : 1;
    }

    private static CapturedImage Crop(CapturedImage src, int x, int y, int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            Array.Copy(src.Bgra, ((y + row) * src.Width + x) * 4, bgra, row * w * 4, w * 4);
        }

        return new CapturedImage(bgra, w, h, new ScreenRect(0, 0, w, h), src.DpiScale);
    }

    /// <summary>Ripete l'immagine a mattonelle fino alle dimensioni richieste.</summary>
    private static CapturedImage Tile(CapturedImage src, int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int sy = y % src.Height;
            for (int x = 0; x < w; x += src.Width)
            {
                int len = Math.Min(src.Width, w - x);
                Array.Copy(src.Bgra, sy * src.Width * 4, bgra, (y * w + x) * 4, len * 4);
            }
        }

        return new CapturedImage(bgra, w, h, new ScreenRect(0, 0, w, h), src.DpiScale);
    }
}
