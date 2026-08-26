using Telekinesis.Abstractions;

namespace Telekinesis.Linux;

/// <summary>
/// Maps the AT-SPI state bitfield (returned by GetState as an array of two
/// uint32 words, one bit per ATSPI_STATE_* index) to normalized ElementState.
/// </summary>
internal static class AtSpiStateMap
{
    // ATSPI_STATE_* bit indices we care about (stable, from the AT-SPI spec).
    private const int Active = 1;
    private const int Checked = 4;
    private const int Collapsed = 5;
    private const int Editable = 7;
    private const int Enabled = 8;
    private const int Expanded = 10;
    private const int Focusable = 11;
    private const int Focused = 12;
    private const int Modal = 16;
    private const int Selectable = 22;
    private const int Selected = 23;
    private const int Sensitive = 24;
    private const int Showing = 25;
    private const int Visible = 30;

    private static bool Has(uint[] words, int bit)
    {
        int word = bit / 32;
        return word < words.Length && (words[word] & (1u << (bit % 32))) != 0;
    }

    public static ElementState Map(uint[] words)
    {
        var s = ElementState.None;
        if (Has(words, Enabled) || Has(words, Sensitive)) s |= ElementState.Enabled;
        if (Has(words, Visible)) s |= ElementState.Visible;
        if (!Has(words, Showing)) s |= ElementState.Offscreen;
        if (Has(words, Focusable)) s |= ElementState.Focusable;
        if (Has(words, Focused)) s |= ElementState.Focused;
        if (Has(words, Selectable)) s |= ElementState.Selectable;
        if (Has(words, Selected)) s |= ElementState.Selected;
        if (Has(words, Checked)) s |= ElementState.Checked;
        if (Has(words, Expanded)) s |= ElementState.Expanded;
        if (Has(words, Collapsed)) s |= ElementState.Collapsed;
        if (Has(words, Editable)) s |= ElementState.Editable;
        if (Has(words, Modal)) s |= ElementState.Modal;
        if (Has(words, Active)) s |= ElementState.Active;
        return s;
    }
}
