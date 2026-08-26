using System.Windows.Automation;
using Telekinesis.Abstractions;

namespace Telekinesis.Windows;

/// <summary>
/// Maps UIA ControlTypes to normalized roles. AccessibleRole was modeled on UIA,
/// so this is nearly 1:1; the few UIA-only types (Thumb, TitleBar, Calendar, …)
/// fall through to Unknown with the LocalizedControlType preserved in NativeRole.
/// </summary>
internal static class UiaRoleMap
{
    private static readonly Dictionary<int, AccessibleRole> Map = new()
    {
        [ControlType.Button.Id] = AccessibleRole.Button,
        [ControlType.SplitButton.Id] = AccessibleRole.Button,
        [ControlType.CheckBox.Id] = AccessibleRole.CheckBox,
        [ControlType.RadioButton.Id] = AccessibleRole.RadioButton,
        [ControlType.ComboBox.Id] = AccessibleRole.ComboBox,
        [ControlType.Edit.Id] = AccessibleRole.Edit,
        [ControlType.Text.Id] = AccessibleRole.Text,
        [ControlType.Hyperlink.Id] = AccessibleRole.Link,
        [ControlType.Image.Id] = AccessibleRole.Image,
        [ControlType.List.Id] = AccessibleRole.List,
        [ControlType.ListItem.Id] = AccessibleRole.ListItem,
        [ControlType.Tree.Id] = AccessibleRole.Tree,
        [ControlType.TreeItem.Id] = AccessibleRole.TreeItem,
        [ControlType.DataGrid.Id] = AccessibleRole.Table,
        [ControlType.Table.Id] = AccessibleRole.Table,
        [ControlType.DataItem.Id] = AccessibleRole.TableRow,
        [ControlType.Header.Id] = AccessibleRole.Header,
        [ControlType.HeaderItem.Id] = AccessibleRole.Header,
        [ControlType.Menu.Id] = AccessibleRole.Menu,
        [ControlType.MenuBar.Id] = AccessibleRole.MenuBar,
        [ControlType.MenuItem.Id] = AccessibleRole.MenuItem,
        [ControlType.Tab.Id] = AccessibleRole.Tab,
        [ControlType.TabItem.Id] = AccessibleRole.TabItem,
        [ControlType.ToolBar.Id] = AccessibleRole.ToolBar,
        [ControlType.StatusBar.Id] = AccessibleRole.StatusBar,
        [ControlType.Slider.Id] = AccessibleRole.Slider,
        [ControlType.Spinner.Id] = AccessibleRole.Slider,
        [ControlType.ProgressBar.Id] = AccessibleRole.ProgressBar,
        [ControlType.ScrollBar.Id] = AccessibleRole.ScrollBar,
        [ControlType.Separator.Id] = AccessibleRole.Separator,
        [ControlType.ToolTip.Id] = AccessibleRole.ToolTip,
        [ControlType.Window.Id] = AccessibleRole.Window,
        [ControlType.Pane.Id] = AccessibleRole.Pane,
        [ControlType.Group.Id] = AccessibleRole.Group,
        [ControlType.Document.Id] = AccessibleRole.Document,
    };

    public static AccessibleRole Normalize(ControlType? controlType) =>
        controlType is null ? AccessibleRole.Unknown : Map.GetValueOrDefault(controlType.Id, AccessibleRole.Unknown);
}
