using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.App.CommandLine;

/// <summary>Uscita JSON della riga di comando: rientrata, lettere accentate leggibili, enum come stringhe.</summary>
internal static class CliJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Print(object value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value, value.GetType(), Options));
        Console.Out.Flush();
    }

    public static object Error(string command, string message) => new { command, ok = false, error = message };

    public static object Rect(ImageRect r) => new { x = Math.Round(r.X, 1), y = Math.Round(r.Y, 1), width = Math.Round(r.Width, 1), height = Math.Round(r.Height, 1) };

    public static object Rect(ScreenRect r) => new { x = r.X, y = r.Y, width = r.Width, height = r.Height };

    public static object Point(ScreenPoint p) => new { x = p.X, y = p.Y };

    public static object Lines(OcrResult result) => result.Lines.Select(l => new
    {
        text = l.Text,
        box = Rect(l.Box),
        confidence = l.Confidence is double c ? Math.Round(c, 3) : (double?)null,
        words = l.Words.Count,
    }).ToArray();
}
