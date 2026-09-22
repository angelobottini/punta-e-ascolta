using System.Text;
using static PuntaEAscolta.App.Native.NativeMethods;

namespace PuntaEAscolta.App.Hosting;

/// <summary>
/// Uscita UTF-8 per la riga di comando. L'eseguibile è WinExe (nessuna console propria): se stdout è già rediretto
/// (pipe o file) si usa quello, altrimenti ci si aggancia alla console di chi ha lanciato il comando.
/// </summary>
internal static class ConsoleSetup
{
    private static uint s_originalCodePage;
    private static bool s_attached;

    public static void Initialize()
    {
        try
        {
            if (!HasUsableHandle(StdOutputHandle))
            {
                s_attached = AttachConsole(AttachParentProcess);
            }

            if (GetConsoleWindow() != 0)
            {
                s_originalCodePage = GetConsoleOutputCP();
                if (s_originalCodePage != Utf8CodePage) SetConsoleOutputCP(Utf8CodePage);
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            if (Console.IsInputRedirected)
            {
                Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
            }
        }
        catch (Exception)
        {
            // Nessuna console disponibile: l'uscita va persa, il codice di uscita resta valido.
        }
    }

    /// <summary>Ripristina la tabella codici della console di chi ha lanciato il comando.</summary>
    public static void Restore()
    {
        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
            if (s_originalCodePage != 0 && s_originalCodePage != Utf8CodePage) SetConsoleOutputCP(s_originalCodePage);
            if (s_attached)
            {
                // Con la console agganciata il prompt di cmd è già stato stampato: una riga vuota lo fa ricomparire.
                Console.Out.WriteLine();
            }
        }
        catch (Exception)
        {
            // ignorato
        }
    }

    private static bool HasUsableHandle(int which)
    {
        nint handle = GetStdHandle(which);
        return handle != 0 && handle != -1 && GetFileType(handle) != 0;
    }
}
