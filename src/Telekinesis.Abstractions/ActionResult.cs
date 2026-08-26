namespace Telekinesis.Abstractions;

/// <summary>How an action was ultimately performed.</summary>
public enum ActionPath
{
    /// <summary>Via the platform accessibility action API (AT-SPI Action, UIA pattern, AXPress).</summary>
    NativeAction,
    /// <summary>Via OS-level input injection at the element's coordinates (uinput, SendInput, CGEvent).</summary>
    InputInjection,
}

public sealed record ActionResult(bool Success, ActionPath Path, string? Error = null)
{
    public static ActionResult Native() => new(true, ActionPath.NativeAction);
    public static ActionResult Injected() => new(true, ActionPath.InputInjection);
    public static ActionResult Failed(ActionPath path, string error) => new(false, path, error);
}

public sealed record AccessibilityEvent(
    string Kind,          // "focus-changed", "state-changed", "window-activated", ...
    ElementRef? Element,
    DateTimeOffset Timestamp);

/// <summary>Result of an environment diagnosis (the `doctor` command).</summary>
public sealed record DiagnosticReport(bool Ready, IReadOnlyList<DiagnosticItem> Items);

public sealed record DiagnosticItem(string Check, bool Ok, string Detail, string? Remedy = null);
