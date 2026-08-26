using System.Collections.Concurrent;
using Telekinesis.Abstractions;

namespace Telekinesis.MacOS;

/// <summary>
/// macOS backend: an Accessibility API (AXUIElement) client via P/Invoke into
/// ApplicationServices. Perception reads the AX tree; actions use AXPerformAction /
/// AXSetAttributeValue natively, falling back to CGEvent injection. Requires the process
/// to hold Accessibility (TCC) permission — see DiagnoseAsync.
/// </summary>
public sealed class AxBackend : IAccessibilityBackend
{
    // Opaque element-handle table: our string ids -> retained AXUIElementRef. AX elements
    // have no re-resolvable path, so we retain refs and hand out ids; actions re-resolve
    // here and surface StaleElementException when AX reports the element is gone.
    private readonly ConcurrentDictionary<string, IntPtr> _handles = new();
    private readonly ConcurrentDictionary<string, IntPtr> _attrCache = new();
    private long _counter;

    public string Name => "AXAPI (macOS)";

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The AXAPI backend requires macOS.");
        return Task.CompletedTask;
    }

    public Task<DiagnosticReport> DiagnoseAsync(CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();
        if (!OperatingSystem.IsMacOS())
        {
            items.Add(new("platform", false, "Not running on macOS.", "Use the Linux or Windows backend."));
            return Task.FromResult(new DiagnosticReport(false, items));
        }
        var trusted = Ax.AXIsProcessTrusted();
        items.Add(new("accessibility-permission", trusted,
            trusted ? "Accessibility (TCC) permission granted." : "Accessibility permission NOT granted — AX calls will fail.",
            trusted ? null : "Grant it in System Settings > Privacy & Security > Accessibility for this terminal/binary. "
                + "Note: permission resets when the binary's code signature changes."));
        if (trusted)
        {
            var apps = ListApplicationsInternal();
            items.Add(new("ax", true, $"Accessibility reachable; {apps.Count} application(s) with windows."));
        }
        return Task.FromResult(new DiagnosticReport(trusted, items));
    }

    // ---- Perception ----

    public Task<IReadOnlyList<ApplicationInfo>> ListApplicationsAsync(CancellationToken ct = default)
        => Task.FromResult(ListApplicationsInternal());

    private IReadOnlyList<ApplicationInfo> ListApplicationsInternal()
    {
        var seen = new Dictionary<int, string>();
        var info = Ax.CGWindowListCopyWindowInfo(
            Ax.kCGWindowListOptionOnScreenOnly | Ax.kCGWindowListExcludeDesktopElements, 0);
        if (info == IntPtr.Zero) return Array.Empty<ApplicationInfo>();
        try
        {
            var pidKey = CF.CFStr(Ax.kCGWindowOwnerPID);
            var nameKey = CF.CFStr(Ax.kCGWindowOwnerName);
            var count = CF.CFArrayGetCount(info);
            for (nint i = 0; i < count; i++)
            {
                var dict = CF.CFArrayGetValueAtIndex(info, i);
                var pid = (int)CF.ToLong(CF.CFDictionaryGetValue(dict, pidKey));
                if (pid == 0 || seen.ContainsKey(pid)) continue;
                var name = CF.ToString(CF.CFDictionaryGetValue(dict, nameKey)) ?? $"pid {pid}";
                seen[pid] = name;
            }
            CF.ReleaseIf(pidKey);
            CF.ReleaseIf(nameKey);
        }
        finally { CF.ReleaseIf(info); }

        return seen.Select(kv => new ApplicationInfo(kv.Key.ToString(), kv.Value, kv.Key)).ToList();
    }

    public Task<AccessibleElement> GetTreeAsync(string applicationId, int maxDepth = 3, CancellationToken ct = default)
    {
        var pid = ParsePid(applicationId);
        var app = Ax.AXUIElementCreateApplication(pid); // owned
        try { return Task.FromResult(ReadNode(app, pid, maxDepth, ct)); }
        finally { CF.ReleaseIf(app); }
    }

    public Task<IReadOnlyList<AccessibleElement>> FindElementsAsync(ElementQuery query, CancellationToken ct = default)
    {
        const int NodeCap = 20_000;
        var results = new List<AccessibleElement>();
        var visited = 0;
        var queue = new Queue<(IntPtr El, int Pid)>();

        IEnumerable<int> pids = query.ApplicationId is { } a
            ? new[] { ParsePid(a) }
            : ListApplicationsInternal().Select(x => x.ProcessId ?? 0).Where(p => p != 0);
        var apps = new List<IntPtr>();
        foreach (var pid in pids)
        {
            var app = Ax.AXUIElementCreateApplication(pid);
            apps.Add(app);
            queue.Enqueue((app, pid));
        }

        try
        {
            while (queue.Count > 0 && results.Count < query.MaxResults && visited < NodeCap)
            {
                ct.ThrowIfCancellationRequested();
                var (el, pid) = queue.Dequeue();
                visited++;

                var role = GetStringAttr(el, Ax.kAXRoleAttribute) ?? "";
                var normalized = AxRoleMap.Normalize(role);
                var name = GetName(el);
                if (Matches(query, normalized, name))
                {
                    var states = ReadStates(el, normalized, GetStringAttr(el, Ax.kAXSubroleAttribute));
                    if (query.WithStates is not { } req || (states & req) == req)
                        results.Add(BuildElement(el, pid, role, normalized, name, states, GetBounds(el), null, 0, null));
                }
                foreach (var child in CopyChildren(el))
                    queue.Enqueue((child, pid)); // retained; released via handle table lifetime
            }
        }
        finally { foreach (var app in apps) CF.ReleaseIf(app); }

        return Task.FromResult((IReadOnlyList<AccessibleElement>)results);
    }

    public Task<AccessibleElement> ReadElementAsync(ElementRef element, CancellationToken ct = default)
    {
        var el = Resolve(element);
        var pid = ParsePid(element.ApplicationId);
        return Task.FromResult(ReadNode(el, pid, maxDepth: 0, ct, reuseId: element.Id));
    }

    public Task<AccessibleElement?> GetFocusedAsync(CancellationToken ct = default)
    {
        var sys = Ax.AXUIElementCreateSystemWide();
        try
        {
            var app = CopyAttr(sys, Ax.kAXFocusedApplicationAttribute);
            if (app == IntPtr.Zero) return Task.FromResult<AccessibleElement?>(null);
            try
            {
                var focused = CopyAttr(app, Ax.kAXFocusedUIElementAttribute);
                if (focused == IntPtr.Zero) return Task.FromResult<AccessibleElement?>(null);
                try
                {
                    Ax.AXUIElementGetPid(focused, out var pid);
                    return Task.FromResult<AccessibleElement?>(ReadNode(focused, pid, 0, ct));
                }
                finally { CF.ReleaseIf(focused); }
            }
            finally { CF.ReleaseIf(app); }
        }
        finally { CF.ReleaseIf(sys); }
    }

    public async Task<AccessibilityEvent?> WaitForEventAsync(string kind, TimeSpan timeout, CancellationToken ct = default)
    {
        // Polling implementation (an AXObserver run-loop is a later optimization). Detects
        // focus-changed by watching the focused element's identity.
        var start = await GetFocusedAsync(ct);
        var baseline = (start?.Role, start?.Name);
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(120, ct);
            var now = await GetFocusedAsync(ct);
            if ((now?.Role, now?.Name) != baseline)
                return new AccessibilityEvent("focus-changed", now?.Ref, DateTimeOffset.Now);
        }
        return null;
    }

    // ---- Actions ----

    public Task<ActionResult> InvokeAsync(ElementRef element, string? action = null, CancellationToken ct = default)
    {
        var el = Resolve(element);
        var press = AttrName(Ax.kAXPressAction);
        var rc = Ax.AXUIElementPerformAction(el, press);
        if (rc == Ax.Success) return Task.FromResult(ActionResult.Native());
        return ClickAsync(element, PointerButton.Left, ct);
    }

    public async Task<ActionResult> SetTextAsync(ElementRef element, string text, CancellationToken ct = default)
    {
        var el = Resolve(element);
        var value = CF.CFStr(text);
        try
        {
            var rc = Ax.AXUIElementSetAttributeValue(el, AttrName(Ax.kAXValueAttribute), value);
            if (rc == Ax.Success) return ActionResult.Native();
        }
        finally { CF.ReleaseIf(value); }
        var click = await ClickAsync(element, PointerButton.Left, ct);
        if (!click.Success) return click;
        await TypeTextAsync(text, ct);
        return ActionResult.Injected();
    }

    public Task<ActionResult> SetValueAsync(ElementRef element, double value, CancellationToken ct = default)
    {
        var el = Resolve(element);
        var num = CF.Number(value);
        try
        {
            var rc = Ax.AXUIElementSetAttributeValue(el, AttrName(Ax.kAXValueAttribute), num);
            return Task.FromResult(rc == Ax.Success
                ? ActionResult.Native()
                : ActionResult.Failed(ActionPath.NativeAction, $"AXSetAttributeValue failed ({rc})."));
        }
        finally { CF.ReleaseIf(num); }
    }

    public Task<ActionResult> ClickAsync(ElementRef element, PointerButton button = PointerButton.Left, CancellationToken ct = default)
    {
        var el = Resolve(element);
        var bounds = GetBounds(el);
        if (bounds is null)
            return Task.FromResult(ActionResult.Failed(ActionPath.InputInjection, "Element has no on-screen bounds."));
        var pt = new CGPoint(bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0);
        var (down, up, btn) = button switch
        {
            PointerButton.Right => (Ax.kCGEventRightMouseDown, Ax.kCGEventRightMouseUp, Ax.kCGMouseButtonRight),
            PointerButton.Middle => (Ax.kCGEventOtherMouseDown, Ax.kCGEventOtherMouseUp, Ax.kCGMouseButtonCenter),
            _ => (Ax.kCGEventLeftMouseDown, Ax.kCGEventLeftMouseUp, Ax.kCGMouseButtonLeft),
        };
        Post(Ax.CGEventCreateMouseEvent(IntPtr.Zero, down, pt, btn));
        Post(Ax.CGEventCreateMouseEvent(IntPtr.Zero, up, pt, btn));
        return Task.FromResult(ActionResult.Injected());
    }

    public Task<ActionResult> TypeTextAsync(string text, CancellationToken ct = default)
    {
        foreach (var ch in text)
        {
            var units = new ushort[] { ch };
            var down = Ax.CGEventCreateKeyboardEvent(IntPtr.Zero, 0, true);
            Ax.CGEventKeyboardSetUnicodeString(down, units.Length, units);
            Post(down);
            var up = Ax.CGEventCreateKeyboardEvent(IntPtr.Zero, 0, false);
            Ax.CGEventKeyboardSetUnicodeString(up, units.Length, units);
            Post(up);
        }
        return Task.FromResult(ActionResult.Injected());
    }

    public Task<ActionResult> PressKeysAsync(string combination, CancellationToken ct = default)
    {
        // Minimal keycode map; extend as needed. Modifiers handled via CGEventSetFlags.
        var parts = combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ulong flags = 0;
        ushort? key = null;
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "cmd" or "meta" or "super": flags |= 0x100000; break; // maskCommand
                case "shift": flags |= 0x20000; break;
                case "alt" or "option": flags |= 0x80000; break;
                case "ctrl" or "control": flags |= 0x40000; break;
                default:
                    if (MacKeyMap.TryGet(p, out var kc)) key = kc;
                    else return Task.FromResult(ActionResult.Failed(ActionPath.InputInjection, $"Unknown key '{p}'."));
                    break;
            }
        }
        if (key is not { } code)
            return Task.FromResult(ActionResult.Failed(ActionPath.InputInjection, "No key in combination."));
        var down = Ax.CGEventCreateKeyboardEvent(IntPtr.Zero, code, true);
        if (flags != 0) Ax.CGEventSetFlags(down, flags);
        Post(down);
        var up = Ax.CGEventCreateKeyboardEvent(IntPtr.Zero, code, false);
        if (flags != 0) Ax.CGEventSetFlags(up, flags);
        Post(up);
        return Task.FromResult(ActionResult.Injected());
    }

    public ValueTask DisposeAsync()
    {
        foreach (var h in _handles.Values) CF.ReleaseIf(h);
        foreach (var a in _attrCache.Values) CF.ReleaseIf(a);
        _handles.Clear();
        return ValueTask.CompletedTask;
    }

    // ---- helpers ----

    private static void Post(IntPtr ev)
    {
        if (ev == IntPtr.Zero) return;
        Ax.CGEventPost(Ax.kCGHIDEventTap, ev);
        CF.ReleaseIf(ev);
    }

    /// <summary>Cached owned CFString for an attribute/action name.</summary>
    private IntPtr AttrName(string name) => _attrCache.GetOrAdd(name, CF.CFStr);

    private IntPtr CopyAttr(IntPtr el, string attr)
        => Ax.AXUIElementCopyAttributeValue(el, AttrName(attr), out var v) == Ax.Success ? v : IntPtr.Zero;

    private string? GetStringAttr(IntPtr el, string attr)
    {
        var v = CopyAttr(el, attr);
        if (v == IntPtr.Zero) return null;
        try { return CF.IsString(v) ? CF.ToString(v) : null; }
        finally { CF.ReleaseIf(v); }
    }

    private bool GetBoolAttr(IntPtr el, string attr)
    {
        var v = CopyAttr(el, attr);
        if (v == IntPtr.Zero) return false;
        try { return CF.ToBool(v); }
        finally { CF.ReleaseIf(v); }
    }

    private string? GetName(IntPtr el) =>
        GetStringAttr(el, Ax.kAXTitleAttribute) is { Length: > 0 } t ? t
        : GetStringAttr(el, Ax.kAXDescriptionAttribute);

    private Bounds? GetBounds(IntPtr el)
    {
        var posV = CopyAttr(el, Ax.kAXPositionAttribute);
        var sizeV = CopyAttr(el, Ax.kAXSizeAttribute);
        try
        {
            if (posV == IntPtr.Zero || sizeV == IntPtr.Zero) return null;
            if (!Ax.AXValueGetValue(posV, Ax.kAXValueCGPointType, out CGPoint p)) return null;
            if (!Ax.AXValueGetValue(sizeV, Ax.kAXValueCGSizeType, out CGSize s)) return null;
            int x = (int)p.X, y = (int)p.Y, w = (int)s.Width, h = (int)s.Height;
            const int Max = 100_000;
            if (w <= 0 || h <= 0 || w > Max || h > Max || x < -Max || x > Max || y < -Max || y > Max)
                return null;
            return new Bounds(x, y, w, h);
        }
        finally { CF.ReleaseIf(posV); CF.ReleaseIf(sizeV); }
    }

    private ElementState ReadStates(IntPtr el, AccessibleRole role, string? subrole)
    {
        var s = ElementState.None;
        if (GetBoolAttr(el, Ax.kAXEnabledAttribute)) s |= ElementState.Enabled;
        if (GetBoolAttr(el, Ax.kAXFocusedAttribute)) s |= ElementState.Focused;
        if (GetBoolAttr(el, Ax.kAXSelectedAttribute)) s |= ElementState.Selected;
        if (GetBoolAttr(el, Ax.kAXExpandedAttribute)) s |= ElementState.Expanded;
        s |= GetBoolAttr(el, Ax.kAXHiddenAttribute) ? ElementState.Offscreen : ElementState.Visible;
        if (role is AccessibleRole.Edit or AccessibleRole.Text) s |= ElementState.Editable;
        if (subrole == "AXSecureTextField") s |= ElementState.Protected;
        return s;
    }

    private List<IntPtr> CopyChildren(IntPtr el)
    {
        var list = new List<IntPtr>();
        var arr = CopyAttr(el, Ax.kAXChildrenAttribute);
        if (arr == IntPtr.Zero) return list;
        try
        {
            if (!CF.IsArray(arr)) return list;
            var count = CF.CFArrayGetCount(arr);
            for (nint i = 0; i < count; i++)
            {
                var child = CF.CFArrayGetValueAtIndex(arr, i);
                if (child != IntPtr.Zero) list.Add(CF.CFRetain(child)); // survive array release
            }
        }
        finally { CF.ReleaseIf(arr); }
        return list;
    }

    private AccessibleElement ReadNode(IntPtr el, int pid, int maxDepth, CancellationToken ct, string? reuseId = null)
    {
        ct.ThrowIfCancellationRequested();
        var role = GetStringAttr(el, Ax.kAXRoleAttribute) ?? "AXUnknown";
        var subrole = GetStringAttr(el, Ax.kAXSubroleAttribute);
        var normalized = AxRoleMap.Normalize(role);
        var name = GetName(el);
        var states = ReadStates(el, normalized, subrole);
        var bounds = GetBounds(el);

        var isProtected = (states & ElementState.Protected) != 0;
        string? text = null;
        if (!isProtected && normalized is AccessibleRole.Edit or AccessibleRole.Text or AccessibleRole.Document)
            text = GetStringAttr(el, Ax.kAXValueAttribute);

        var children = CopyChildren(el);
        List<AccessibleElement>? childNodes = null;
        if (maxDepth > 0 && children.Count > 0)
        {
            childNodes = new List<AccessibleElement>(children.Count);
            foreach (var c in children) childNodes.Add(ReadNode(c, pid, maxDepth - 1, ct));
        }

        return BuildElement(el, pid, role, normalized, name, states, bounds, text, children.Count, childNodes, reuseId);
    }

    private AccessibleElement BuildElement(IntPtr el, int pid, string nativeRole, AccessibleRole role,
        string? name, ElementState states, Bounds? bounds, string? text, int childCount,
        List<AccessibleElement>? children, string? reuseId = null)
    {
        return new AccessibleElement
        {
            Ref = Register(el, pid, reuseId),
            Role = role,
            NativeRole = nativeRole,
            Name = string.IsNullOrEmpty(name) ? null : name,
            States = states,
            Bounds = bounds,
            Text = text,
            ChildCount = childCount,
            Children = children,
        };
    }

    private ElementRef Register(IntPtr el, int pid, string? reuseId)
    {
        if (reuseId is not null) return new ElementRef(reuseId, pid.ToString());
        var id = $"{pid}:{System.Threading.Interlocked.Increment(ref _counter)}";
        _handles[id] = CF.CFRetain(el);
        return new ElementRef(id, pid.ToString());
    }

    private IntPtr Resolve(ElementRef element)
    {
        if (_handles.TryGetValue(element.Id, out var ptr) && ptr != IntPtr.Zero) return ptr;
        throw new StaleElementException(element);
    }

    private static int ParsePid(string applicationId) =>
        int.TryParse(applicationId.Split(':')[0], out var pid) ? pid
            : throw new ArgumentException($"Invalid application id '{applicationId}'.");

    private static bool Matches(ElementQuery q, AccessibleRole role, string? name)
    {
        if (q.Role is { } r && role != r) return false;
        if (!string.IsNullOrEmpty(q.NameContains) &&
            (name is null || name.IndexOf(q.NameContains, StringComparison.OrdinalIgnoreCase) < 0))
            return false;
        if (q.Role is null && string.IsNullOrEmpty(q.NameContains)) return !string.IsNullOrEmpty(name);
        return true;
    }
}
