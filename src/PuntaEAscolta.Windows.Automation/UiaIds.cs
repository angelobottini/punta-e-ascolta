namespace PuntaEAscolta.Windows.Automation;

/// <summary>Identificatori numerici di UI Automation (proprietà, pattern, tipi di controllo) usati dal modulo.</summary>
internal static class UiaIds
{
    // Proprietà
    public const int BoundingRectangle = 30001;
    public const int ProcessId = 30002;
    public const int ControlType = 30003;
    public const int Name = 30005;
    public const int AcceleratorKey = 30006;
    public const int AccessKey = 30007;
    public const int IsEnabled = 30010;
    public const int AutomationId = 30011;
    public const int ClassName = 30012;
    public const int HelpText = 30013;
    public const int LabeledBy = 30018;
    public const int IsPassword = 30019;
    public const int NativeWindowHandle = 30020;
    public const int IsOffscreen = 30022;
    public const int FrameworkId = 30024;
    public const int ItemStatus = 30026;
    public const int IsTextPatternAvailable = 30040;
    public const int ValueValue = 30045;
    public const int ToggleToggleState = 30086;
    public const int FullDescription = 30159;

    // Pattern
    public const int ValuePattern = 10002;
    public const int TextPattern = 10014;
    public const int TogglePattern = 10015;
    public const int LegacyIAccessiblePattern = 10018;

    // Tipi di controllo
    public const int ButtonControl = 50000;
    public const int CalendarControl = 50001;
    public const int CheckBoxControl = 50002;
    public const int ComboBoxControl = 50003;
    public const int EditControl = 50004;
    public const int HyperlinkControl = 50005;
    public const int ImageControl = 50006;
    public const int ListItemControl = 50007;
    public const int ListControl = 50008;
    public const int MenuControl = 50009;
    public const int MenuBarControl = 50010;
    public const int MenuItemControl = 50011;
    public const int ProgressBarControl = 50012;
    public const int RadioButtonControl = 50013;
    public const int ScrollBarControl = 50014;
    public const int SliderControl = 50015;
    public const int SpinnerControl = 50016;
    public const int StatusBarControl = 50017;
    public const int TabControl = 50018;
    public const int TabItemControl = 50019;
    public const int TextControl = 50020;
    public const int ToolBarControl = 50021;
    public const int ToolTipControl = 50022;
    public const int TreeControl = 50023;
    public const int TreeItemControl = 50024;
    public const int CustomControl = 50025;
    public const int GroupControl = 50026;
    public const int ThumbControl = 50027;
    public const int DataGridControl = 50028;
    public const int DataItemControl = 50029;
    public const int DocumentControl = 50030;
    public const int SplitButtonControl = 50031;
    public const int WindowControl = 50032;
    public const int PaneControl = 50033;
    public const int HeaderControl = 50034;
    public const int HeaderItemControl = 50035;
    public const int TableControl = 50036;
    public const int TitleBarControl = 50037;
    public const int SeparatorControl = 50038;
    public const int SemanticZoomControl = 50039;
    public const int AppBarControl = 50040;

    /// <summary>
    /// Proprietà lette in un solo giro cross-process con ElementFromPointBuildCache. Le proprietà dei pattern vanno richieste
    /// a parte: AddPattern mette in cache solo l'oggetto pattern, e senza ValueValue e ToggleToggleState CachedValue e
    /// CachedToggleState lanciano E_INVALIDARG (verificato: le celle di Excel risultavano sempre vuote).
    /// </summary>
    public static readonly int[] CachedProperties =
    [
        ControlType, Name, ClassName, FrameworkId, AutomationId, BoundingRectangle, IsEnabled, IsOffscreen,
        HelpText, FullDescription, ItemStatus, AcceleratorKey, AccessKey, IsPassword, LabeledBy, ProcessId,
        NativeWindowHandle, IsTextPatternAvailable, ValueValue, ToggleToggleState,
    ];

    /// <summary>Pattern inseriti nella cache: Value, Toggle, LegacyIAccessible, Text.</summary>
    public static readonly int[] CachedPatterns = [ValuePattern, TogglePattern, LegacyIAccessiblePattern, TextPattern];
}
