using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;

namespace PuntaEAscolta.Logic.Resolution;

/// <summary>Esito della decisione su un elemento dell'interfaccia. Text è già ripulito e pronto per la voce.</summary>
public sealed record UiResolution(ReadSource Source, string Text, SpeechKind Kind, bool Sensitive);

/// <summary>Decide che cosa pronunciare a partire dai dati raccolti dall'accessibilità. null = passare a tooltip e OCR. Vedi docs/DESIGN.md sez. 3.1.</summary>
public static class UiTextResolver
{
    public static UiResolution? Resolve(UiElementInfo info, ReadingSettings settings) => throw new NotImplementedException();
}
