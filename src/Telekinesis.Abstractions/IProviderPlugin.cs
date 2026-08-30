namespace Telekinesis.Abstractions;

/// <summary>
/// A provider plugin: an optional, higher-fidelity perception/action provider
/// for the applications it claims. The OS backend is the base of the ladder;
/// plugins wrap it for specific app classes (browsers, canvas apps) without
/// bloating the OS-agnostic core, and the resolver picks the highest-priority
/// plugin that claims the target — falling back to the base backend.
///
/// Prefer MCP composition (connecting a second, independent tool server) when
/// the goal is just "add a tool"; a provider plugin is for keeping ONE unified
/// find/invoke surface that transparently upgrades per app.
/// </summary>
public interface IProviderPlugin
{
    /// <summary>Short stable name, shown by doctor.</summary>
    string Name { get; }

    /// <summary>Higher wins when several plugins claim the same application.</summary>
    int Priority { get; }

    /// <summary>Whether this plugin wants to handle the given application.</summary>
    bool Handles(ApplicationInfo app);

    /// <summary>
    /// Return the backend to use for a claimed application — typically a
    /// <see cref="DelegatingAccessibilityBackend"/> overriding a few members.
    /// Returning <paramref name="baseBackend"/> unchanged is valid.
    /// </summary>
    IAccessibilityBackend Wrap(IAccessibilityBackend baseBackend, ApplicationInfo app);

    /// <summary>
    /// Extra MCP tool classes this plugin contributes ([McpServerToolType]
    /// statics). Built-in plugins' tools load with the perception set; tools
    /// from external plugins are only exposed when actions are enabled.
    /// </summary>
    IEnumerable<Type> ToolTypes => [];
}

/// <summary>
/// Convenience base for plugin backends: delegates every member to the wrapped
/// backend so a plugin overrides only what it upgrades. Does NOT dispose the
/// inner backend — its lifetime belongs to the provider that created it.
/// </summary>
public abstract class DelegatingAccessibilityBackend(IAccessibilityBackend inner) : IAccessibilityBackend
{
    protected IAccessibilityBackend Inner { get; } = inner;

    public virtual string Name => Inner.Name;
    public virtual Task ConnectAsync(CancellationToken ct = default) => Inner.ConnectAsync(ct);
    public virtual Task<DiagnosticReport> DiagnoseAsync(CancellationToken ct = default) => Inner.DiagnoseAsync(ct);
    public virtual Task<IReadOnlyList<ApplicationInfo>> ListApplicationsAsync(CancellationToken ct = default) => Inner.ListApplicationsAsync(ct);
    public virtual Task<AccessibleElement> GetTreeAsync(string applicationId, int maxDepth = 3, CancellationToken ct = default) => Inner.GetTreeAsync(applicationId, maxDepth, ct);
    public virtual Task<AccessibleElement> GetSubtreeAsync(ElementRef element, int maxDepth = 3, CancellationToken ct = default) => Inner.GetSubtreeAsync(element, maxDepth, ct);
    public virtual Task<IReadOnlyList<AccessibleElement>> FindElementsAsync(ElementQuery query, CancellationToken ct = default) => Inner.FindElementsAsync(query, ct);
    public virtual Task<AccessibleElement> ReadElementAsync(ElementRef element, CancellationToken ct = default) => Inner.ReadElementAsync(element, ct);
    public virtual Task<AccessibleElement?> GetFocusedAsync(CancellationToken ct = default) => Inner.GetFocusedAsync(ct);
    public virtual Task<AccessibilityEvent?> WaitForEventAsync(string kind, TimeSpan timeout, CancellationToken ct = default) => Inner.WaitForEventAsync(kind, timeout, ct);
    public virtual Task<ActionResult> InvokeAsync(ElementRef element, string? action = null, CancellationToken ct = default) => Inner.InvokeAsync(element, action, ct);
    public virtual Task<ActionResult> SetTextAsync(ElementRef element, string text, CancellationToken ct = default) => Inner.SetTextAsync(element, text, ct);
    public virtual Task<ActionResult> SetValueAsync(ElementRef element, double value, CancellationToken ct = default) => Inner.SetValueAsync(element, value, ct);
    public virtual Task<ActionResult> ClickAsync(ElementRef element, PointerButton button = PointerButton.Left, CancellationToken ct = default) => Inner.ClickAsync(element, button, ct);
    public virtual Task<ActionResult> TypeTextAsync(string text, CancellationToken ct = default) => Inner.TypeTextAsync(text, ct);
    public virtual Task<ActionResult> PressKeysAsync(string combination, CancellationToken ct = default) => Inner.PressKeysAsync(combination, ct);
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
