namespace Telekinesis.Abstractions;

/// <summary>
/// Normalized element states. Backends map platform state sets
/// (AT-SPI StateSet, UIA properties, AXAPI attributes) into these flags.
/// </summary>
[Flags]
public enum ElementState : long
{
    None          = 0,
    Enabled       = 1 << 0,
    Visible       = 1 << 1,
    Focusable     = 1 << 2,
    Focused       = 1 << 3,
    Selected      = 1 << 4,
    Selectable    = 1 << 5,
    Checked       = 1 << 6,
    Expanded      = 1 << 7,
    Collapsed     = 1 << 8,
    Editable      = 1 << 9,
    ReadOnly      = 1 << 10,
    Offscreen     = 1 << 11,
    Modal         = 1 << 12,
    Active        = 1 << 13,
    /// <summary>Content is masked (password fields). Text is never exposed for these.</summary>
    Protected     = 1 << 14,
}
