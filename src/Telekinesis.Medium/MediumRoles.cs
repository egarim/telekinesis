using Telekinesis.Abstractions;

namespace Telekinesis.Medium;

/// <summary>
/// Maps Medium's role strings onto the normalized accessibility role vocabulary, so
/// Medium metadata can be associated with a runtime element. Medium roles may use
/// framework vocabulary ("textbox", "checkbox"); the map keeps those synonyms aligned
/// with <see cref="AccessibleRole"/> for matching/disambiguation.
/// </summary>
public static class MediumRoles
{
    private static readonly Dictionary<string, AccessibleRole> RoleMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["button"] = AccessibleRole.Button,
        ["checkbox"] = AccessibleRole.CheckBox,
        ["check"] = AccessibleRole.CheckBox,
        ["combobox"] = AccessibleRole.ComboBox,
        ["dropdown"] = AccessibleRole.ComboBox,
        ["edit"] = AccessibleRole.Edit,
        ["textbox"] = AccessibleRole.Edit,
        ["text"] = AccessibleRole.Edit,
        ["header"] = AccessibleRole.Header,
        ["image"] = AccessibleRole.Image,
        ["label"] = AccessibleRole.Label,
        ["link"] = AccessibleRole.Link,
        ["list"] = AccessibleRole.List,
        ["listitem"] = AccessibleRole.ListItem,
        ["menuitem"] = AccessibleRole.MenuItem,
        ["progressbar"] = AccessibleRole.ProgressBar,
        ["radiobutton"] = AccessibleRole.RadioButton,
        ["radio"] = AccessibleRole.RadioButton,
        ["separator"] = AccessibleRole.Separator,
        ["slider"] = AccessibleRole.Slider,
        ["tab"] = AccessibleRole.Tab,
        ["tabitem"] = AccessibleRole.TabItem,
        ["table"] = AccessibleRole.Table,
        ["tablecell"] = AccessibleRole.TableCell,
        ["tablerow"] = AccessibleRole.TableRow,
        ["treeitem"] = AccessibleRole.TreeItem,
        ["window"] = AccessibleRole.Window,
    };

    /// <summary>Map a Medium role string to an <see cref="AccessibleRole"/>, or null when unknown.</summary>
    public static AccessibleRole? Map(string? role) =>
        string.IsNullOrWhiteSpace(role) ? null : (RoleMap.TryGetValue(role.Trim(), out var r) ? r : null);

    /// <summary>Whether a Medium role is plausibly the same control as the given element.</summary>
    public static bool IsCompatible(string? mediumRole, AccessibleElement element) =>
        Map(mediumRole) is { } mapped && mapped == element.Role;
}
