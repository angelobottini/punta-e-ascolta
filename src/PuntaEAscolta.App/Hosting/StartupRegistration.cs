using Microsoft.Win32;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.App.Hosting;

/// <summary>Avvio con Windows tramite la chiave Run dell'utente corrente (nessun privilegio, nessun servizio).</summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PuntaEAscolta";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PuntaEAscolta.exe");

    private static string CommandLine => "\"" + ExecutablePath + "\"";

    /// <summary>Valore attuale della chiave Run per l'app, null se assente.</summary>
    public static string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsRegisteredForThisCopy() =>
        string.Equals(GetRegisteredCommand(), CommandLine, StringComparison.OrdinalIgnoreCase);

    /// <summary>Scrive o toglie il valore nella chiave Run. Restituisce false (e registra) in caso di errore.</summary>
    public static bool Apply(bool enabled, ILog log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                if (!string.Equals(key.GetValue(ValueName) as string, CommandLine, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
                    log.Info($"Avvio con Windows attivato: {CommandLine}");
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                log.Info("Avvio con Windows disattivato");
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Impossibile aggiornare l'avvio con Windows", ex);
            return false;
        }
    }
}
