using System.Windows;
using PuntaEAscolta.App.Hosting;
using Forms = System.Windows.Forms;

namespace PuntaEAscolta.App.Tray;

/// <summary>
/// Modalità normale: istanza singola, composizione, icona di notifica e ciclo dei messaggi WPF senza finestra principale.
/// Silenziosa all'avvio (parla solo se ci sono problemi di attivazione, ad esempio una scorciatoia già occupata).
/// </summary>
internal static class TrayHost
{
    public static int Run()
    {
        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirst)
        {
            // Già in esecuzione: la prima istanza apre le impostazioni.
            SingleInstance.SignalFirstInstance();
            return 0;
        }

        var log = new AppLog();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.Error("Eccezione non gestita: l'app si chiude", e.ExceptionObject as Exception);
            log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("Eccezione di un'attività in sottofondo non osservata", e.Exception);
            e.SetObserved();
        };

        AppServices? services = null;
        TrayController? controller = null;
        try
        {
            services = AppServices.Create(log);

            Forms.Application.EnableVisualStyles();
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.CatchException);
            Forms.Application.ThreadException += (_, e) => log.Error("Errore nell'icona di notifica", e.Exception);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                log.Error("Errore nell'interfaccia", e.Exception);
                e.Handled = true;
            };
            app.SessionEnding += (_, _) =>
            {
                log.Info("Fine della sessione di Windows: chiusura");
                app.Shutdown();
            };

            controller = new TrayController(services, app, instance);
            controller.Start();
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Avvio di Punta e Ascolta non riuscito", ex);
            return 1;
        }
        finally
        {
            try { controller?.Dispose(); } catch (Exception ex) { log.Error("Chiusura dell'icona di notifica", ex); }
            services?.Dispose();
            log.Info("Punta e Ascolta chiuso");
            log.Dispose();
        }
    }
}
