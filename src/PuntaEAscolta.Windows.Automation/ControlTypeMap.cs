using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Automation;

/// <summary>Traduzione dei ControlType di UI Automation nel tipo astratto UiElementKind.</summary>
internal static class ControlTypeMap
{
    public static UiElementKind ToKind(int controlTypeId) => controlTypeId switch
    {
        UiaIds.ButtonControl => UiElementKind.Button,
        UiaIds.CalendarControl => UiElementKind.Pane,
        UiaIds.CheckBoxControl => UiElementKind.CheckBox,
        UiaIds.ComboBoxControl => UiElementKind.ComboBox,
        UiaIds.EditControl => UiElementKind.Edit,
        UiaIds.HyperlinkControl => UiElementKind.Hyperlink,
        UiaIds.ImageControl => UiElementKind.Image,
        UiaIds.ListItemControl => UiElementKind.ListItem,
        UiaIds.ListControl => UiElementKind.List,
        UiaIds.MenuControl => UiElementKind.Menu,
        UiaIds.MenuBarControl => UiElementKind.MenuBar,
        UiaIds.MenuItemControl => UiElementKind.MenuItem,
        UiaIds.ProgressBarControl => UiElementKind.ProgressBar,
        UiaIds.RadioButtonControl => UiElementKind.RadioButton,
        UiaIds.ScrollBarControl => UiElementKind.ScrollBar,
        UiaIds.SliderControl => UiElementKind.Slider,
        UiaIds.SpinnerControl => UiElementKind.Spinner,
        UiaIds.StatusBarControl => UiElementKind.StatusBar,
        UiaIds.TabControl => UiElementKind.Tab,
        UiaIds.TabItemControl => UiElementKind.TabItem,
        UiaIds.TextControl => UiElementKind.Text,
        UiaIds.ToolBarControl => UiElementKind.ToolBar,
        UiaIds.ToolTipControl => UiElementKind.ToolTip,
        UiaIds.TreeControl => UiElementKind.Tree,
        UiaIds.TreeItemControl => UiElementKind.TreeItem,
        UiaIds.CustomControl => UiElementKind.Custom,
        UiaIds.GroupControl => UiElementKind.Group,
        UiaIds.ThumbControl => UiElementKind.Unknown,
        UiaIds.DataGridControl => UiElementKind.DataGrid,
        UiaIds.DataItemControl => UiElementKind.DataItem,
        UiaIds.DocumentControl => UiElementKind.Document,
        UiaIds.SplitButtonControl => UiElementKind.SplitButton,
        UiaIds.WindowControl => UiElementKind.Window,
        UiaIds.PaneControl => UiElementKind.Pane,
        UiaIds.HeaderControl => UiElementKind.Header,
        UiaIds.HeaderItemControl => UiElementKind.HeaderItem,
        UiaIds.TableControl => UiElementKind.DataGrid,
        UiaIds.TitleBarControl => UiElementKind.TitleBar,
        // I separatori di menu si presentano come MenuItem senza nome: la logica li tratta come silenzio.
        UiaIds.SeparatorControl => UiElementKind.MenuItem,
        UiaIds.SemanticZoomControl => UiElementKind.Unknown,
        UiaIds.AppBarControl => UiElementKind.ToolBar,
        _ => UiElementKind.Unknown,
    };

    /// <summary>Controlli "con etichetta" ai quali si risale quando il puntatore colpisce il loro figlio Text o Image.</summary>
    public static bool IsLabelledControl(UiElementKind kind) => kind is UiElementKind.MenuItem or UiElementKind.TabItem
        or UiElementKind.ListItem or UiElementKind.Button or UiElementKind.SplitButton or UiElementKind.CheckBox
        or UiElementKind.RadioButton or UiElementKind.TreeItem or UiElementKind.Hyperlink or UiElementKind.HeaderItem;

    /// <summary>Tipi che possono ospitare un TextPattern da cui estrarre il paragrafo sotto il punto.</summary>
    public static bool IsTextHostKind(UiElementKind kind) => kind is UiElementKind.Document or UiElementKind.Edit
        or UiElementKind.DataItem or UiElementKind.Custom or UiElementKind.Pane;

    /// <summary>Tipi passivi dai quali ha senso risalire fino a un ospite di testo (testo di una pagina web, di un PDF, di Word).</summary>
    public static bool MayClimbToTextHost(UiElementKind kind) => kind is UiElementKind.Text or UiElementKind.Hyperlink
        or UiElementKind.Image or UiElementKind.Group or UiElementKind.Custom or UiElementKind.Pane
        or UiElementKind.Document or UiElementKind.Edit or UiElementKind.Unknown;
}
