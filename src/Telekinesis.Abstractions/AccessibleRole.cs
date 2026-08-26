namespace Telekinesis.Abstractions;

/// <summary>
/// Normalized control roles across platforms. Modeled on UIA ControlTypes (the
/// richest of the three vendor vocabularies); AT-SPI roles and AXAPI AXRoles are
/// mapped into this set by their backends. When a native role has no clean match,
/// backends use <see cref="Unknown"/> and preserve the original in
/// <see cref="AccessibleElement.NativeRole"/>.
/// </summary>
public enum AccessibleRole
{
    Unknown = 0,
    Application,
    Window,
    Pane,
    Group,
    Document,
    Button,
    CheckBox,
    RadioButton,
    ComboBox,
    Edit,
    PasswordEdit,
    Text,
    Label,
    Link,
    Image,
    List,
    ListItem,
    Tree,
    TreeItem,
    Table,
    TableRow,
    TableCell,
    Header,
    Menu,
    MenuBar,
    MenuItem,
    Tab,
    TabItem,
    ToolBar,
    StatusBar,
    Slider,
    ProgressBar,
    ScrollBar,
    Separator,
    ToolTip,
    Dialog,
}
