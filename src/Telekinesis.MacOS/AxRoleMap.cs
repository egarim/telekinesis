using Telekinesis.Abstractions;

namespace Telekinesis.MacOS;

/// <summary>Maps AXRole strings to the normalized role vocabulary.</summary>
internal static class AxRoleMap
{
    private static readonly Dictionary<string, AccessibleRole> Map = new(StringComparer.Ordinal)
    {
        ["AXApplication"] = AccessibleRole.Application,
        ["AXWindow"] = AccessibleRole.Window,
        ["AXSheet"] = AccessibleRole.Dialog,
        ["AXDrawer"] = AccessibleRole.Pane,
        ["AXGroup"] = AccessibleRole.Group,
        ["AXSplitGroup"] = AccessibleRole.Group,
        ["AXScrollArea"] = AccessibleRole.Pane,
        ["AXWebArea"] = AccessibleRole.Document,
        ["AXButton"] = AccessibleRole.Button,
        ["AXPopUpButton"] = AccessibleRole.ComboBox,
        ["AXMenuButton"] = AccessibleRole.Button,
        ["AXCheckBox"] = AccessibleRole.CheckBox,
        ["AXRadioButton"] = AccessibleRole.RadioButton,
        ["AXComboBox"] = AccessibleRole.ComboBox,
        ["AXTextField"] = AccessibleRole.Edit,
        ["AXTextArea"] = AccessibleRole.Edit,
        ["AXStaticText"] = AccessibleRole.Text,
        ["AXHeading"] = AccessibleRole.Header,
        ["AXLink"] = AccessibleRole.Link,
        ["AXImage"] = AccessibleRole.Image,
        ["AXList"] = AccessibleRole.List,
        ["AXRow"] = AccessibleRole.ListItem,
        ["AXTable"] = AccessibleRole.Table,
        ["AXCell"] = AccessibleRole.TableCell,
        ["AXOutline"] = AccessibleRole.Tree,
        ["AXMenu"] = AccessibleRole.Menu,
        ["AXMenuBar"] = AccessibleRole.MenuBar,
        ["AXMenuItem"] = AccessibleRole.MenuItem,
        ["AXMenuBarItem"] = AccessibleRole.MenuItem,
        ["AXTabGroup"] = AccessibleRole.Tab,
        ["AXRadioGroup"] = AccessibleRole.Group,
        ["AXToolbar"] = AccessibleRole.ToolBar,
        ["AXSlider"] = AccessibleRole.Slider,
        ["AXProgressIndicator"] = AccessibleRole.ProgressBar,
        ["AXScrollBar"] = AccessibleRole.ScrollBar,
        ["AXSplitter"] = AccessibleRole.Separator,
    };

    public static AccessibleRole Normalize(string axRole) =>
        Map.GetValueOrDefault(axRole, AccessibleRole.Unknown);
}
