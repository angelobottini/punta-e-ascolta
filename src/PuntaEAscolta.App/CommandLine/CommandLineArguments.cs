using System.Globalization;
using PuntaEAscolta.Core;

namespace PuntaEAscolta.App.CommandLine;

internal enum CliCommand { Help, ReadAt, ReadSelection, OcrFile, Speak, Voices, SelfTest, SetKey, Exit, PreviewSettings }

/// <summary>Argomenti già interpretati della riga di comando.</summary>
internal sealed class CommandLineArguments
{
    public CliCommand Command { get; private set; } = CliCommand.Help;

    /// <summary>Punto di --read-at o --point; null = posizione attuale del puntatore.</summary>
    public ScreenPoint? Point { get; private set; }

    public bool Zone { get; private set; }
    public bool SpeakResult { get; private set; }
    public string? File { get; private set; }
    public string Engine { get; private set; } = "windows";
    public double? Scale { get; private set; }
    public string? Text { get; private set; }
    public string? Provider { get; private set; }
    public int DelayMs { get; private set; }
    public bool Verbose { get; private set; }
    public bool Verify { get; private set; }

    /// <summary>Interpreta gli argomenti; in caso di errore restituisce null e il messaggio (in italiano).</summary>
    public static CommandLineArguments? Parse(string[] args, out string? error)
    {
        error = null;
        var result = new CommandLineArguments();
        CliCommand? command = null;
        bool hasReadCommand = args.Any(a => Is(a, "--read-at") || Is(a, "--read-selection"));

        bool SetCommand(CliCommand c, out string? err)
        {
            err = null;
            if (command is not null && command != c)
            {
                err = "Indicare un solo comando per volta (vedi --help).";
                return false;
            }
            command = c;
            return true;
        }

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            bool nextIsValue = next is not null && !next.StartsWith("--", StringComparison.Ordinal);

            switch (a.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                case "-?":
                case "/?":
                case "--aiuto":
                    if (!SetCommand(CliCommand.Help, out error)) return null;
                    break;

                case "--read-at":
                    if (!SetCommand(CliCommand.ReadAt, out error)) return null;
                    if (nextIsValue && !IsPointerKeyword(next!))
                    {
                        if (!TryParsePoint(args, ref i, out var p, out error)) return null;
                        result.Point = p;
                    }
                    else if (nextIsValue)
                    {
                        i++; // "puntatore": posizione attuale
                    }
                    break;

                case "--read-selection":
                    if (!SetCommand(CliCommand.ReadSelection, out error)) return null;
                    break;

                case "--zone":
                case "--zona":
                    result.Zone = true;
                    break;

                case "--speak":
                case "--leggi":
                    if (hasReadCommand)
                    {
                        result.SpeakResult = true;
                    }
                    else
                    {
                        if (!SetCommand(CliCommand.Speak, out error)) return null;
                        if (next is null)
                        {
                            error = "--speak richiede il testo da pronunciare, tra virgolette.";
                            return null;
                        }
                        result.Text = next;
                        i++;
                    }
                    break;

                case "--ocr-file":
                    if (!SetCommand(CliCommand.OcrFile, out error)) return null;
                    if (!nextIsValue)
                    {
                        error = "--ocr-file richiede il percorso di un'immagine PNG.";
                        return null;
                    }
                    result.File = next;
                    i++;
                    break;

                case "--point":
                case "--punto":
                    if (!nextIsValue || !TryParsePoint(args, ref i, out var point, out error))
                    {
                        error ??= "--point richiede le coordinate X,Y.";
                        return null;
                    }
                    result.Point = point;
                    break;

                case "--engine":
                case "--motore":
                    if (!nextIsValue)
                    {
                        error = "--engine richiede windows, onnx oppure entrambi.";
                        return null;
                    }
                    string engine = next!.ToLowerInvariant();
                    if (engine is "both" or "all" or "tutti") engine = "entrambi";
                    if (engine is not ("windows" or "onnx" or "entrambi"))
                    {
                        error = $"Motore OCR sconosciuto: {next}. Valori ammessi: windows, onnx, entrambi.";
                        return null;
                    }
                    result.Engine = engine;
                    i++;
                    break;

                case "--scale":
                case "--scala":
                    if (!nextIsValue || !TryParseDouble(next!, out double scale) || scale is < 0.5 or > 5)
                    {
                        error = "--scale richiede un numero fra 0,5 e 5 (es. 1.25).";
                        return null;
                    }
                    result.Scale = scale;
                    i++;
                    break;

                case "--provider":
                case "--fornitore":
                    if (!nextIsValue)
                    {
                        error = "--provider richiede windows, elevenlabs oppure auto.";
                        return null;
                    }
                    string provider = next!.ToLowerInvariant();
                    if (provider is not ("windows" or "elevenlabs" or "auto"))
                    {
                        error = $"Fornitore sconosciuto: {next}. Valori ammessi: windows, elevenlabs, auto.";
                        return null;
                    }
                    result.Provider = provider;
                    i++;
                    break;

                case "--wait":
                case "--attendi":
                    if (!nextIsValue || !int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay) || delay is < 0 or > 60000)
                    {
                        error = "--attendi richiede un numero di millisecondi fra 0 e 60000.";
                        return null;
                    }
                    result.DelayMs = delay;
                    i++;
                    break;

                case "--voices":
                case "--voci":
                    if (!SetCommand(CliCommand.Voices, out error)) return null;
                    break;

                case "--selftest":
                case "--autodiagnosi":
                    if (!SetCommand(CliCommand.SelfTest, out error)) return null;
                    break;

                case "--set-key":
                case "--imposta-chiave":
                    if (!SetCommand(CliCommand.SetKey, out error)) return null;
                    break;

                case "--exit":
                case "--esci":
                    if (!SetCommand(CliCommand.Exit, out error)) return null;
                    break;

                case "--preview-settings":
                case "--anteprima-impostazioni":
                    if (!SetCommand(CliCommand.PreviewSettings, out error)) return null;
                    if (!nextIsValue)
                    {
                        error = "--anteprima-impostazioni richiede la cartella dove salvare le immagini.";
                        return null;
                    }
                    result.File = next;
                    i++;
                    break;

                case "--verify":
                case "--verifica":
                    result.Verify = true;
                    break;

                case "--verbose":
                case "--dettagli":
                    result.Verbose = true;
                    break;

                default:
                    error = $"Argomento non riconosciuto: {a} (vedi --help).";
                    return null;
            }
        }

        if (command is null)
        {
            error = "Nessun comando indicato (vedi --help).";
            return null;
        }
        result.Command = command.Value;

        if (result.Command == CliCommand.OcrFile && result.Point is null)
        {
            error = "--ocr-file richiede anche --point X,Y (coordinate in pixel dell'immagine).";
            return null;
        }
        return result;
    }

    private static bool Is(string arg, string name) => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsPointerKeyword(string value) =>
        value.Equals("puntatore", StringComparison.OrdinalIgnoreCase) || value.Equals("pointer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Accetta "X,Y" (anche "X;Y") in un solo argomento, oppure "X Y" in due argomenti.</summary>
    private static bool TryParsePoint(string[] args, ref int i, out ScreenPoint point, out string? error)
    {
        point = default;
        error = null;
        string first = args[i + 1];
        var parts = first.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && TryParseInt(parts[0], out int x) && TryParseInt(parts[1], out int y))
        {
            point = new ScreenPoint(x, y);
            i += 1;
            return true;
        }
        if (parts.Length == 1 && TryParseInt(parts[0], out x) && i + 2 < args.Length && TryParseInt(args[i + 2], out y))
        {
            point = new ScreenPoint(x, y);
            i += 2;
            return true;
        }
        error = $"Coordinate non valide: {first}. Usare X,Y (pixel fisici), ad esempio 120,300.";
        return false;
    }

    private static bool TryParseInt(string s, out int value)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d))
        {
            value = (int)Math.Round(d);
            return true;
        }
        return false;
    }

    private static bool TryParseDouble(string s, out double value) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
