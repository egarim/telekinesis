namespace Telekinesis.Abstractions;

/// <summary>
/// A stable, re-resolvable handle to an element. Native accessibility object
/// identities churn as UIs rebuild, so Telekinesis issues its own ids and
/// re-resolves them on every action, failing loudly when the element is gone.
/// </summary>
/// <param name="Id">Opaque Telekinesis element id (backend-scoped).</param>
/// <param name="ApplicationId">Id of the owning application.</param>
public sealed record ElementRef(string Id, string ApplicationId);

public sealed record Bounds(int X, int Y, int Width, int Height);

/// <summary>A node in the normalized accessibility tree.</summary>
public sealed record AccessibleElement
{
    public required ElementRef Ref { get; init; }
    public required AccessibleRole Role { get; init; }
    /// <summary>The platform's original role string, for when the normalization leaks.</summary>
    public required string NativeRole { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public ElementState States { get; init; }
    /// <summary>Screen bounds in device-independent pixels; null when not on screen.</summary>
    public Bounds? Bounds { get; init; }
    /// <summary>Text content. Always null when <see cref="ElementState.Protected"/> is set.</summary>
    public string? Text { get; init; }
    /// <summary>Current value for value-bearing controls (sliders, progress bars).</summary>
    public double? Value { get; init; }
    /// <summary>Names of native actions this element supports (e.g. "invoke", "expand").</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];
    public int ChildCount { get; init; }
    /// <summary>Populated only by tree queries, up to the requested depth.</summary>
    public IReadOnlyList<AccessibleElement>? Children { get; init; }
}

public sealed record ApplicationInfo(string Id, string Name, int? ProcessId);

/// <summary>Search criteria for <see cref="IAccessibilityBackend.FindElementsAsync"/>.</summary>
public sealed record ElementQuery
{
    /// <summary>Restrict to one application; null searches every application on the bus.</summary>
    public string? ApplicationId { get; init; }
    /// <summary>Restrict to the subtree rooted at this element (e.g. a browser
    /// page's Document node). Takes precedence over <see cref="ApplicationId"/> seeding.</summary>
    public ElementRef? Within { get; init; }
    /// <summary>Do not descend into <see cref="AccessibleRole.Document"/> subtrees —
    /// searches only the application's own chrome (address bar, tabs, toolbars).
    /// Document nodes themselves still match.</summary>
    public bool ExcludeDocumentContent { get; init; }
    public AccessibleRole? Role { get; init; }
    /// <summary>Case-insensitive substring match on Name.</summary>
    public string? NameContains { get; init; }
    public ElementState? WithStates { get; init; }
    public int MaxResults { get; init; } = 25;
}
