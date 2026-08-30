namespace Telekinesis.Abstractions;

/// <summary>
/// The OS-agnostic contract every platform backend implements.
/// Linux: AT-SPI over D-Bus. Windows: UI Automation. macOS: AXAPI.
/// The MCP server (and any other consumer) is written only against this interface.
/// </summary>
public interface IAccessibilityBackend : IAsyncDisposable
{
    /// <summary>Human-readable backend name, e.g. "AT-SPI (Linux)".</summary>
    string Name { get; }

    /// <summary>Connect to the platform accessibility infrastructure.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Check permissions, bus availability, injection capability. Never throws.</summary>
    Task<DiagnosticReport> DiagnoseAsync(CancellationToken ct = default);

    // ---- Perception (clairvoyant mode) ----

    Task<IReadOnlyList<ApplicationInfo>> ListApplicationsAsync(CancellationToken ct = default);

    /// <summary>Depth-limited subtree. Never returns unbounded trees.</summary>
    Task<AccessibleElement> GetTreeAsync(string applicationId, int maxDepth = 3, CancellationToken ct = default);

    /// <summary>
    /// Depth-limited subtree rooted at a previously returned element — the
    /// drill-down primitive for deep trees (browser pages live many levels below
    /// the window). Throws <see cref="StaleElementException"/> when the element is gone.
    /// </summary>
    Task<AccessibleElement> GetSubtreeAsync(ElementRef element, int maxDepth = 3, CancellationToken ct = default);

    Task<IReadOnlyList<AccessibleElement>> FindElementsAsync(ElementQuery query, CancellationToken ct = default);

    /// <summary>Re-resolve a handle and return full current detail; throws <see cref="StaleElementException"/> if gone.</summary>
    Task<AccessibleElement> ReadElementAsync(ElementRef element, CancellationToken ct = default);

    /// <summary>The currently focused element, or null when nothing reports focus.</summary>
    Task<AccessibleElement?> GetFocusedAsync(CancellationToken ct = default);

    /// <summary>Await the next matching event, e.g. after an action, to verify its effect.</summary>
    Task<AccessibilityEvent?> WaitForEventAsync(string kind, TimeSpan timeout, CancellationToken ct = default);

    // ---- Action (telekinesis mode) ----
    // Implementations try the native accessibility action first and fall back to
    // input injection, reporting which path was used in the ActionResult.

    Task<ActionResult> InvokeAsync(ElementRef element, string? action = null, CancellationToken ct = default);

    Task<ActionResult> SetTextAsync(ElementRef element, string text, CancellationToken ct = default);

    Task<ActionResult> SetValueAsync(ElementRef element, double value, CancellationToken ct = default);

    /// <summary>Pointer click at the element's center (always the injection path).</summary>
    Task<ActionResult> ClickAsync(ElementRef element, PointerButton button = PointerButton.Left, CancellationToken ct = default);

    /// <summary>Type text into the focused element via input injection.</summary>
    Task<ActionResult> TypeTextAsync(string text, CancellationToken ct = default);

    /// <summary>Press a key combination, e.g. "ctrl+s", "alt+F4".</summary>
    Task<ActionResult> PressKeysAsync(string combination, CancellationToken ct = default);
}

public enum PointerButton { Left, Middle, Right }

/// <summary>Thrown when an <see cref="ElementRef"/> no longer resolves to a live element.</summary>
public sealed class StaleElementException(ElementRef element)
    : Exception($"Element '{element.Id}' in application '{element.ApplicationId}' no longer exists; re-query before acting.")
{
    public ElementRef Element { get; } = element;
}
