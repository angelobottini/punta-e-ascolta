using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using UIA = Interop.UIAutomationClient;

namespace PuntaEAscolta.Windows.Automation;

/// <summary>Proprietà di un elemento lette dalla cache (nessun giro cross-process). Ogni lettura è protetta.</summary>
internal sealed class ElementSnapshot
{
    private const double MaxCoordinate = 1_000_000;

    public required UIA.IUIAutomationElement Element { get; init; }
    public int ControlTypeId { get; init; }
    public UiElementKind Kind { get; init; }
    public string? Name { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public string? AutomationId { get; init; }
    public string? HelpText { get; init; }
    public string? FullDescription { get; init; }
    public string? ItemStatus { get; init; }
    public string? AcceleratorKey { get; init; }
    public string? AccessKey { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsOffscreen { get; init; }
    public bool IsPassword { get; init; }
    public ScreenRect Bounds { get; init; }
    public int ProcessId { get; init; }
    public bool HasTextPattern { get; init; }
    public UIA.IUIAutomationElement? LabeledBy { get; init; }

    public static ElementSnapshot Read(UIA.IUIAutomationElement el)
    {
        int controlType = Safe(() => el.CachedControlType, 0);
        return new ElementSnapshot
        {
            Element = el,
            ControlTypeId = controlType,
            Kind = ControlTypeMap.ToKind(controlType),
            Name = Clean(Safe(() => el.CachedName, null)),
            ClassName = Clean(Safe(() => el.CachedClassName, null)),
            FrameworkId = Clean(Safe(() => el.CachedFrameworkId, null)),
            AutomationId = Clean(Safe(() => el.CachedAutomationId, null)),
            HelpText = Clean(Safe(() => el.CachedHelpText, null)),
            FullDescription = Clean(Safe(() => el.GetCachedPropertyValue(UiaIds.FullDescription) as string, null)),
            ItemStatus = Clean(Safe(() => el.CachedItemStatus, null)),
            AcceleratorKey = Clean(Safe(() => el.CachedAcceleratorKey, null)),
            AccessKey = Clean(Safe(() => el.CachedAccessKey, null)),
            IsEnabled = Safe(() => el.CachedIsEnabled != 0, true),
            IsOffscreen = Safe(() => el.CachedIsOffscreen != 0, false),
            IsPassword = Safe(() => el.CachedIsPassword != 0, false),
            Bounds = Safe(() => ToScreenRect(el.CachedBoundingRectangle), default),
            ProcessId = Safe(() => el.CachedProcessId, 0),
            HasTextPattern = Safe(() => el.GetCachedPropertyValue(UiaIds.IsTextPatternAvailable) is bool b && b, false),
            LabeledBy = Safe(() => el.CachedLabeledBy, null),
        };
    }

    /// <summary>Rettangolo in pixel fisici; vuoto (default) se degenere o fuori scala.</summary>
    public static ScreenRect ToScreenRect(UIA.tagRECT rc) =>
        ToScreenRect(rc.left, rc.top, rc.right, rc.bottom);

    public static ScreenRect ToScreenRect(double left, double top, double right, double bottom)
    {
        if (!IsSane(left) || !IsSane(top) || !IsSane(right) || !IsSane(bottom)) return default;
        if (right <= left || bottom <= top) return default;
        return ScreenRect.FromLtrb((int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(right), (int)Math.Round(bottom));
    }

    /// <summary>Converte l'array piatto [x, y, w, h, ...] dei TextRange in rettangoli validi.</summary>
    public static List<ScreenRect> ToScreenRects(double[]? flat)
    {
        var list = new List<ScreenRect>();
        if (flat is null) return list;
        for (int i = 0; i + 3 < flat.Length; i += 4)
        {
            var r = ToScreenRect(flat[i], flat[i + 1], flat[i] + flat[i + 2], flat[i + 1] + flat[i + 3]);
            if (!r.IsEmpty) list.Add(r);
        }
        return list;
    }

    private static bool IsSane(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < MaxCoordinate;

    /// <summary>Stringa vuota o solo spazi -> null; altrimenti la stringa così com'è (la pulizia spetta alla logica).</summary>
    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
