using Telekinesis.Abstractions;

namespace Telekinesis.Linux;

/// <summary>Maps AT-SPI role names (as returned by GetRoleName) to normalized roles.</summary>
internal static class AtSpiRoleMap
{
    private static readonly Dictionary<string, AccessibleRole> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application"] = AccessibleRole.Application,
        ["frame"] = AccessibleRole.Window,
        ["window"] = AccessibleRole.Window,
        ["dialog"] = AccessibleRole.Dialog,
        ["panel"] = AccessibleRole.Pane,
        ["filler"] = AccessibleRole.Group,
        ["section"] = AccessibleRole.Group,
        ["document frame"] = AccessibleRole.Document,
        ["document web"] = AccessibleRole.Document,
        ["push button"] = AccessibleRole.Button,
        ["button"] = AccessibleRole.Button,
        ["toggle button"] = AccessibleRole.Button,
        ["check box"] = AccessibleRole.CheckBox,
        ["radio button"] = AccessibleRole.RadioButton,
        ["combo box"] = AccessibleRole.ComboBox,
        ["entry"] = AccessibleRole.Edit,
        ["text"] = AccessibleRole.Text,
        ["password text"] = AccessibleRole.PasswordEdit,
        ["label"] = AccessibleRole.Label,
        ["link"] = AccessibleRole.Link,
        ["image"] = AccessibleRole.Image,
        ["icon"] = AccessibleRole.Image,
        ["list"] = AccessibleRole.List,
        ["list box"] = AccessibleRole.List,
        ["list item"] = AccessibleRole.ListItem,
        ["tree"] = AccessibleRole.Tree,
        ["tree table"] = AccessibleRole.Tree,
        ["tree item"] = AccessibleRole.TreeItem,
        ["table"] = AccessibleRole.Table,
        ["table row"] = AccessibleRole.TableRow,
        ["table cell"] = AccessibleRole.TableCell,
        ["column header"] = AccessibleRole.Header,
        ["menu"] = AccessibleRole.Menu,
        ["menu bar"] = AccessibleRole.MenuBar,
        ["menu item"] = AccessibleRole.MenuItem,
        ["check menu item"] = AccessibleRole.MenuItem,
        ["page tab list"] = AccessibleRole.Tab,
        ["page tab"] = AccessibleRole.TabItem,
        ["tool bar"] = AccessibleRole.ToolBar,
        ["status bar"] = AccessibleRole.StatusBar,
        ["slider"] = AccessibleRole.Slider,
        ["progress bar"] = AccessibleRole.ProgressBar,
        ["scroll bar"] = AccessibleRole.ScrollBar,
        ["separator"] = AccessibleRole.Separator,
        ["tool tip"] = AccessibleRole.ToolTip,
    };

    public static AccessibleRole Normalize(string atSpiRoleName) =>
        Map.GetValueOrDefault(atSpiRoleName, AccessibleRole.Unknown);
}
