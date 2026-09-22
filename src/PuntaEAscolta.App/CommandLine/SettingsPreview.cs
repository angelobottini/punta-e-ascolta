using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PuntaEAscolta.App.Hosting;
using PuntaEAscolta.App.SettingsUi;
using PuntaEAscolta.Core.Reading;

namespace PuntaEAscolta.App.CommandLine;

/// <summary>
/// Diagnostica per chi sviluppa: costruisce la finestra impostazioni fuori dallo schermo (senza attivarla né mostrarla
/// nella barra delle applicazioni) e salva un'immagine PNG di ogni scheda, così la si può controllare senza disturbare
/// chi usa il PC. Nessuna impostazione viene salvata.
/// </summary>
internal static class SettingsPreview
{
    private const double RenderScale = 1.25;

    public static int Run(string folder, string name, AppLog log)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        using var services = AppServices.Create(log);
        var host = new PreviewHost(services);
        var files = new List<string>();

        var window = new SettingsWindow(host)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            Pump(8);
            files.Add(Save((FrameworkElement)window.Content, Path.Combine(folder, "0-finestra.png")));
            for (int i = 0; i < window.PageCount; i++)
            {
                window.SelectPage(i);
                Pump(6);
                var (header, page) = window.GetPage(i);
                files.Add(Save(page, Path.Combine(folder, $"{i + 1}-{header.ToLowerInvariant()}.png")));
            }
        }
        finally
        {
            window.Close();
            Pump(2);
        }

        CliJson.Print(new { command = name, ok = true, folder, files });
        return 0;
    }

    /// <summary>Lascia lavorare il Dispatcher (impaginazione, caricamenti asincroni della finestra).</summary>
    private static void Pump(int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Thread.Sleep(40);
        }
    }

    /// <summary>Disegna l'elemento intero (anche la parte nascosta dallo scorrimento) in un PNG.</summary>
    private static string Save(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        double width = Math.Max(1, Math.Ceiling(element.ActualWidth));
        double height = Math.Max(1, Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap((int)(width * RenderScale), (int)(height * RenderScale), 96 * RenderScale, 96 * RenderScale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            dc.DrawRectangle(SystemColors.WindowBrush, null, bounds);
            dc.DrawRectangle(new VisualBrush(element)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = bounds,
            }, null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private sealed class PreviewHost(AppServices services) : ISettingsHost
    {
        public AppServices Services { get; } = services;
        public IReadOnlyList<string> InputProblems => Array.Empty<string>();
        public bool Paused => Services.SettingsStore.Current.General.Paused;
        public (ReadOutcome? Outcome, DateTime At) LastOutcome => (null, default);
        public void OpenFolder(string path) { }
        public void OpenLogFolder() { }
    }
}
