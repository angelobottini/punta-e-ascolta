using PuntaEAscolta.App.CommandLine;
using PuntaEAscolta.App.Hosting;
using PuntaEAscolta.App.Tray;

namespace PuntaEAscolta.App;

/// <summary>
/// Punto di ingresso. Senza argomenti: icona nell'area di notifica (uso normale). Con argomenti: modalità di prova a riga
/// di comando, senza interfaccia, con esito JSON su stdout e codice di uscita 0 (riuscito) o 1 (errore).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            ConsoleSetup.Initialize();
            try
            {
                return CommandLineRunner.Run(args);
            }
            finally
            {
                ConsoleSetup.Restore();
            }
        }

        return TrayHost.Run();
    }
}
