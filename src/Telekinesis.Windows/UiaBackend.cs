using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Telekinesis.Abstractions;
using Rect = System.Windows.Rect;

namespace Telekinesis.Windows;

/// <summary>
/// Windows backend: a UI Automation client using the managed
/// System.Windows.Automation API (UIAutomationClient). Perception walks the
/// control view of the UIA tree; actions try native UIA patterns first
/// (Invoke/Value/Toggle/…) and fall back to SendInput injection.
///
/// UIA RuntimeIds churn as UIs rebuild, so this backend issues its own opaque
/// element ids backed by a registry of live AutomationElement handles and
/// re-validates them on every use, throwing <see cref="StaleElementException"/>
/// when the underlying element is gone — same discipline as the Linux backend.
/// </summary>
public sealed class UiaBackend : IAccessibilityBackend, IScreenCaptureBackend, IPointerInjectionBackend, IVisualFeedbackBackend
{
    private const int MaxChildScan = 256;    // per-node child walk cap (huge lists, virtualized grids)
    private const int MaxTextLength = 16384; // TextPattern read cap

    private readonly ConcurrentDictionary<string, AutomationElement> _registry = new();
    private int _nextId;
    private readonly SendInputInjector _injector = new();
    private readonly Lazy<OverlayService> _overlay = new(() => new OverlayService());

    /// <summary>With TELEKINESIS_SHOW_INTENT=1, injected actions flash their target
    /// on screen for a beat before the input lands — the agent telegraphs its moves.</summary>
    private static bool ShowIntent =>
        Environment.GetEnvironmentVariable("TELEKINESIS_SHOW_INTENT") is "1" or "true";

    // Event tracking — mirrors the Linux backend's waiter/TCS design.
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly List<(string Kind, TaskCompletionSource<AccessibilityEvent> Tcs)> _waiters = new();
    private AutomationFocusChangedEventHandler? _focusHandler;
    private bool _eventsReady;

    // DPI awareness that actually took effect after ConnectAsync (0=unaware,
    // 1=system, 2=per-monitor). Only per-monitor keeps element bounds aligned with
    // physical pixels across mixed-scale monitors. -1 = not yet connected.
    private int _dpiAwareness = -1;

    public string Name => "UI Automation (Windows)";

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The UIA backend requires Windows.");

        return Task.Run(() =>
        {
            // Without per-monitor DPI awareness the process gets virtualized
            // coordinates while SendInput uses physical ones — element bounds on
            // scaled secondary monitors drift from true pixels. We try to set it
            // here, but the call FAILS if the process already has a DPI context
            // (e.g. the `dotnet` host that runs the tool build declares one in its
            // apphost manifest — you cannot change it once set). The self-contained
            // single-file exe carries a PerMonitorV2 manifest so it starts correct.
            // Either way we record what actually stuck, so `doctor` can warn.
            EnsureDpiAwareness();

            _ = AutomationElement.RootElement
                ?? throw new InvalidOperationException("UI Automation root element is not available.");
        }, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    public Task<DiagnosticReport> DiagnoseAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var items = new List<DiagnosticItem>();

        if (!OperatingSystem.IsWindows())
        {
            items.Add(new("platform", false, "Not running on Windows.",
                "Use the Linux (AT-SPI) or macOS (AXAPI) backend on this OS."));
            return new DiagnosticReport(false, items);
        }
        items.Add(new("platform", true, "Windows detected."));
        EnsureDpiAwareness();

        try
        {
            var root = AutomationElement.RootElement;
            var count = GetChildren(root).Count;
            items.Add(new("uia-root", true, $"UIA desktop root reachable; {count} top-level element(s)."));
        }
        catch (Exception ex)
        {
            items.Add(new("uia-root", false, $"Cannot access the UIA desktop root: {ex.Message}",
                "UIA needs an interactive desktop session; it is unavailable in session 0 / service contexts."));
        }

        var elevated = IsElevated();
        items.Add(new("elevation", true, elevated
            ? "Process is elevated; elevated applications are reachable."
            : "Process is not elevated; elevated applications' UIA trees and input are blocked (UIPI).",
            elevated ? null : "Run Telekinesis elevated if you need to automate elevated apps."));

        items.Add(new("input", true, "SendInput available; no special permission required on Windows."));

        // Per-monitor-v2 is required for element bounds to match physical pixels on
        // scaled secondary monitors. It's still "ok" on a single-monitor or uniform-
        // scale setup (system-aware agrees there) — but warn so mixed-DPI users know.
        var perMonitor = _dpiAwareness == 2;
        items.Add(new("dpi-awareness", true,
            perMonitor
                ? "Per-monitor-v2 DPI awareness active; element bounds match physical pixels on all monitors."
                : $"Process is {DpiAwarenessName(_dpiAwareness)} DPI-aware, not per-monitor. Fine on one monitor or uniform scaling; on a multi-monitor setup with mixed scale factors, element bounds on secondary monitors can drift from true pixels.",
            perMonitor ? null
                : "Run the self-contained single-file build (it ships a per-monitor-v2 manifest), or launch so the process starts per-monitor-v2 aware — the `dotnet` host fixes a DPI context before the tool can change it."));

        return new DiagnosticReport(items.All(i => i.Ok), items);
    }, ct);

    // ---- Perception ----

    public Task<IReadOnlyList<ApplicationInfo>> ListApplicationsAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        // Applications = top-level children of the desktop root, grouped by process.
        var byPid = new Dictionary<int, ApplicationInfo>();
        foreach (var el in GetChildren(AutomationElement.RootElement))
        {
            ct.ThrowIfCancellationRequested();
            int pid;
            string? windowName;
            try
            {
                pid = el.Current.ProcessId;
                windowName = el.Current.Name;
            }
            catch (ElementNotAvailableException) { continue; }
            if (byPid.ContainsKey(pid)) continue;

            var name = string.IsNullOrEmpty(windowName) ? ProcessName(pid) : windowName;
            byPid[pid] = new ApplicationInfo(Id: $"pid:{pid}", Name: name ?? $"pid:{pid}", ProcessId: pid);
        }
        return (IReadOnlyList<ApplicationInfo>)byPid.Values.ToList();
    }, ct);

    public Task<AccessibleElement> GetTreeAsync(string applicationId, int maxDepth = 3, CancellationToken ct = default) => Task.Run(() =>
    {
        var pid = ParsePid(applicationId);
        var windows = GetChildren(AutomationElement.RootElement)
            .Where(el => { try { return el.Current.ProcessId == pid; } catch (ElementNotAvailableException) { return false; } })
            .ToList();
        if (windows.Count == 0)
            throw new StaleElementException(new ElementRef($"app:{pid}", applicationId));

        // A process can own several top-level windows; expose a synthetic
        // Application root so the tree shape matches the Linux backend's.
        List<AccessibleElement>? children = null;
        if (maxDepth > 0)
        {
            children = new List<AccessibleElement>(windows.Count);
            foreach (var w in windows)
                children.Add(ReadNode(w, maxDepth - 1, ct));
        }
        return new AccessibleElement
        {
            Ref = new ElementRef($"app:{pid}", applicationId),
            Role = AccessibleRole.Application,
            NativeRole = "application",
            Name = ProcessName(pid),
            States = ElementState.Enabled | ElementState.Visible,
            ChildCount = windows.Count,
            Children = children,
        };
    }, ct);

    public Task<AccessibleElement> GetSubtreeAsync(ElementRef element, int maxDepth = 3, CancellationToken ct = default) => Task.Run(() =>
    {
        var el = Resolve(element);
        try
        {
            return ReadNode(el, maxDepth, ct, existingRef: element);
        }
        catch (ElementNotAvailableException)
        {
            _registry.TryRemove(element.Id, out _);
            throw new StaleElementException(element);
        }
    }, ct);

    public Task<IReadOnlyList<AccessibleElement>> FindElementsAsync(ElementQuery query, CancellationToken ct = default) => Task.Run(() =>
    {
        // Breadth-first over the control view, bounded by MaxResults and a node cap
        // so a runaway tree (browsers!) can never hang the call. Matches are returned
        // flat — callers drill down with get_tree/read_element. Same as Linux.
        const int NodeCap = 20_000;
        var results = new List<AccessibleElement>();
        var visited = 0;

        var queue = new Queue<AutomationElement>();
        if (query.Within is { } within)
        {
            queue.Enqueue(Resolve(within));
        }
        else if (query.ApplicationId is { } appId)
        {
            var pid = ParsePid(appId);
            foreach (var el in GetChildren(AutomationElement.RootElement))
                if (TryGet(() => el.Current.ProcessId) == pid)
                    queue.Enqueue(el);
        }
        else
        {
            foreach (var el in GetChildren(AutomationElement.RootElement))
                queue.Enqueue(el);
        }

        while (queue.Count > 0 && results.Count < query.MaxResults && visited < NodeCap)
        {
            ct.ThrowIfCancellationRequested();
            var el = queue.Dequeue();
            visited++;

            var descend = true;
            try
            {
                var role = UiaRoleMap.Normalize(el.Current.ControlType);
                if (query.ExcludeDocumentContent && role == AccessibleRole.Document)
                    descend = false; // chrome-only search: the page lives beneath Documents
                if (role == AccessibleRole.Edit && el.Current.IsPassword) role = AccessibleRole.PasswordEdit;
                var name = el.Current.Name;

                if (Matches(query, role, name))
                {
                    var states = GetStates(el, role);
                    if (query.WithStates is { } required && (states & required) != required)
                    {
                        // filtered out by state; still descend into children below
                    }
                    else
                    {
                        results.Add(new AccessibleElement
                        {
                            Ref = Register(el),
                            Role = role,
                            NativeRole = TryGet(() => el.Current.LocalizedControlType) ?? "unknown",
                            Name = string.IsNullOrEmpty(name) ? null : name,
                            States = states,
                            Bounds = ToBounds(TryGet(() => el.Current.BoundingRectangle)),
                            ChildCount = 0,
                        });
                    }
                }
            }
            catch (ElementNotAvailableException) { continue; }

            if (!descend) continue;
            foreach (var child in GetChildren(el))
                queue.Enqueue(child);
        }

        return (IReadOnlyList<AccessibleElement>)results;
    }, ct);

    private static bool Matches(ElementQuery query, AccessibleRole role, string? name)
    {
        if (query.Role is { } r && role != r) return false;
        if (!string.IsNullOrEmpty(query.NameContains) &&
            (name is null || name.IndexOf(query.NameContains, StringComparison.OrdinalIgnoreCase) < 0))
            return false;
        // With no role and no name filter, only return named elements to avoid flooding.
        if (query.Role is null && string.IsNullOrEmpty(query.NameContains))
            return !string.IsNullOrEmpty(name);
        return true;
    }

    public Task<AccessibleElement> ReadElementAsync(ElementRef element, CancellationToken ct = default) => Task.Run(() =>
    {
        var el = Resolve(element);
        try
        {
            return ReadNode(el, maxDepth: 0, ct, existingRef: element);
        }
        catch (ElementNotAvailableException)
        {
            _registry.TryRemove(element.Id, out _);
            throw new StaleElementException(element);
        }
    }, ct);

    public Task<AccessibleElement?> GetFocusedAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            var el = AutomationElement.FocusedElement;
            return el is null ? null : (AccessibleElement?)ReadNode(el, maxDepth: 0, ct);
        }
        catch (ElementNotAvailableException)
        {
            return (AccessibleElement?)null;
        }
    }, ct);

    public async Task<AccessibilityEvent?> WaitForEventAsync(string kind, TimeSpan timeout, CancellationToken ct = default)
    {
        await EnsureEventsAsync(ct);
        var tcs = new TaskCompletionSource<AccessibilityEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = (Kind: kind, Tcs: tcs);
        lock (_waiters) _waiters.Add(waiter);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timed out
        }
        finally
        {
            lock (_waiters) _waiters.Remove(waiter);
        }
    }

    // ---- Actions ----

    public Task<ActionResult> InvokeAsync(ElementRef element, string? action = null, CancellationToken ct = default) => Task.Run(async () =>
    {
        var el = Resolve(element);
        try
        {
            switch (action)
            {
                case null or "invoke" or "click" or "press":
                    if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
                    { ((InvokePattern)inv).Invoke(); return ActionResult.Native(); }
                    if (action is null && el.TryGetCurrentPattern(TogglePattern.Pattern, out var tgl))
                    { ((TogglePattern)tgl).Toggle(); return ActionResult.Native(); }
                    if (action is null && el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var seld))
                    { ((SelectionItemPattern)seld).Select(); return ActionResult.Native(); }
                    break;
                case "toggle":
                    if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var t))
                    { ((TogglePattern)t).Toggle(); return ActionResult.Native(); }
                    break;
                case "expand":
                    if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ex))
                    { ((ExpandCollapsePattern)ex).Expand(); return ActionResult.Native(); }
                    break;
                case "collapse":
                    if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var col))
                    { ((ExpandCollapsePattern)col).Collapse(); return ActionResult.Native(); }
                    break;
                case "select":
                    if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
                    { ((SelectionItemPattern)sel).Select(); return ActionResult.Native(); }
                    break;
                default:
                    return ActionResult.Failed(ActionPath.NativeAction, $"Unknown action '{action}'.");
            }
        }
        catch (ElementNotAvailableException)
        {
            throw new StaleElementException(element);
        }
        catch (Exception e) when (e is InvalidOperationException or COMException)
        {
            // Pattern present but refused (disabled control, etc.) — fall through to injection.
        }
        // Fall back to a pointer click at the element's center.
        return await ClickAsync(element, PointerButton.Left, ct);
    }, ct);

    public Task<ActionResult> SetTextAsync(ElementRef element, string text, CancellationToken ct = default) => Task.Run(() =>
    {
        var el = Resolve(element);
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var v))
            {
                var value = (ValuePattern)v;
                if (!value.Current.IsReadOnly)
                {
                    value.SetValue(text);
                    return ActionResult.Native();
                }
            }
        }
        catch (ElementNotAvailableException) { throw new StaleElementException(element); }
        catch (Exception e) when (e is InvalidOperationException or COMException) { /* fall through */ }

        // Fallback: focus via click, select all, type over the selection — same as Linux.
        var bounds = ToBounds(TryGet(() => el.Current.BoundingRectangle));
        if (bounds is null)
            return ActionResult.Failed(ActionPath.InputInjection, "Element has no on-screen bounds to click.");
        try
        {
            FlashIntent(bounds, "type here");
            _injector.MoveTo(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            _injector.Click(PointerButton.Left);
            Thread.Sleep(100); // let focus settle before typing
            _injector.Chord([WindowsKeyMap.VK_CONTROL, (ushort)'A']);
            _injector.TypeText(text);
            return ActionResult.Injected();
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ActionPath.InputInjection, ex.Message);
        }
    }, ct);

    public Task<ActionResult> SetValueAsync(ElementRef element, double value, CancellationToken ct = default) => Task.Run(() =>
    {
        var el = Resolve(element);
        try
        {
            if (el.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rv))
            {
                ((RangeValuePattern)rv).SetValue(value);
                return ActionResult.Native();
            }
        }
        catch (ElementNotAvailableException) { throw new StaleElementException(element); }
        catch (Exception e) when (e is InvalidOperationException or COMException or ArgumentException)
        {
            return ActionResult.Failed(ActionPath.NativeAction, e.Message);
        }
        return ActionResult.Failed(ActionPath.NativeAction,
            "Element does not expose the UIA RangeValue pattern.");
    }, ct);

    public Task<ActionResult> ClickAsync(ElementRef element, PointerButton button = PointerButton.Left, CancellationToken ct = default) => Task.Run(() =>
    {
        var el = Resolve(element);
        var bounds = ToBounds(TryGet(() => el.Current.BoundingRectangle));
        if (bounds is null || bounds.Width <= 0 || bounds.Height <= 0)
            return ActionResult.Failed(ActionPath.InputInjection, "Element has no on-screen bounds to click.");
        var cx = bounds.X + bounds.Width / 2;
        var cy = bounds.Y + bounds.Height / 2;
        // The a11y tree lists occluded elements as Visible; injecting a click at a
        // covered center would hit whatever is on top. Refuse rather than mis-click.
        if (!PointHitsTargetWindow(el, cx, cy))
            return ActionResult.Failed(ActionPath.InputInjection,
                "Element is covered by another window at its click point; a pointer click would hit the wrong target. Bring it to the foreground, or use the native action (invoke/set_value) which does not depend on being on top.");
        try
        {
            FlashIntent(bounds, "click");
            _injector.MoveTo(cx, cy);
            _injector.Click(button);
            return ActionResult.Injected();
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ActionPath.InputInjection, ex.Message);
        }
    }, ct);

    public Task<ActionResult> TypeTextAsync(string text, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            _injector.TypeText(text);
            return ActionResult.Injected();
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ActionPath.InputInjection, ex.Message);
        }
    }, ct);

    public Task<ActionResult> PressKeysAsync(string combination, CancellationToken ct = default) => Task.Run(() =>
    {
        var codes = new List<ushort>();
        foreach (var part in combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (WindowsKeyMap.TryNamedKey(part, out var named)) codes.Add(named);
            else if (part.Length == 1 && WindowsKeyMap.TryChar(part[0], out var vk, out var shift))
            {
                if (shift && !codes.Contains(WindowsKeyMap.VK_SHIFT)) codes.Insert(0, WindowsKeyMap.VK_SHIFT);
                codes.Add(vk);
            }
            else return ActionResult.Failed(ActionPath.InputInjection, $"Unknown key '{part}'.");
        }
        if (codes.Count == 0)
            return ActionResult.Failed(ActionPath.InputInjection, "Empty key combination.");
        try
        {
            _injector.Chord(codes);
            return ActionResult.Injected();
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ActionPath.InputInjection, ex.Message);
        }
    }, ct);

    // ---- X-ray overlay (IVisualFeedbackBackend) ----

    public Task HighlightAsync(IReadOnlyList<HighlightRegion> regions, TimeSpan duration, CancellationToken ct = default)
    {
        _overlay.Value.Show(regions, duration);
        return Task.CompletedTask;
    }

    public Task ClearHighlightsAsync(CancellationToken ct = default)
    {
        if (_overlay.IsValueCreated) _overlay.Value.Clear();
        return Task.CompletedTask;
    }

    /// <summary>Show-intent beat before an injected action: flash the target, give the
    /// viewer a moment to see it, then let the input land while the box is still up.</summary>
    private void FlashIntent(Bounds b, string label)
    {
        if (!ShowIntent) return;
        _overlay.Value.Show([new HighlightRegion(b, label)], TimeSpan.FromMilliseconds(900));
        Thread.Sleep(450);
    }

    // ---- Vision tier (IScreenCaptureBackend / IPointerInjectionBackend) ----

    public Task<ScreenImage> CaptureScreenAsync(Bounds? region = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var vs = SendInputInjector.VirtualScreen();
        var b = region ?? new Bounds(vs.X, vs.Y, vs.Width, vs.Height);
        if (b.Width <= 0 || b.Height <= 0)
            throw new ArgumentException($"Capture region {b.Width}x{b.Height} is empty.");

        // GDI capture: same physical-pixel space as UIA bounds and SendInput
        // (the process is per-monitor-v2 DPI aware from ConnectAsync).
        using var bmp = new System.Drawing.Bitmap(b.Width, b.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        // Raw BitBlt with CAPTUREBLT: layered windows (tooltips, our own X-ray
        // overlay) are invisible to a plain SourceCopy, and Graphics.CopyFromScreen
        // rejects the SourceCopy|CaptureBlt combination as an undefined enum value.
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var dest = g.GetHdc();
            var src = GetDC(0);
            try
            {
                const uint SrcCopyCaptureBlt = 0x00CC0020 | 0x40000000;
                if (!BitBlt(dest, 0, 0, b.Width, b.Height, src, b.X, b.Y, SrcCopyCaptureBlt))
                    throw new InvalidOperationException("BitBlt screen capture failed.");
            }
            finally
            {
                ReleaseDC(0, src);
                g.ReleaseHdc(dest);
            }
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return new ScreenImage(ms.ToArray(), b.Width, b.Height);
    }, ct);

    public Task<ActionResult> ClickAtAsync(int x, int y, PointerButton button = PointerButton.Left, CancellationToken ct = default) => Task.Run(() =>
    {
        var vs = SendInputInjector.VirtualScreen();
        if (x < vs.X || x >= vs.X + vs.Width || y < vs.Y || y >= vs.Y + vs.Height)
            return ActionResult.Failed(ActionPath.InputInjection,
                $"({x},{y}) is outside the virtual desktop {vs.Width}x{vs.Height} at ({vs.X},{vs.Y}).");
        try
        {
            FlashIntent(new Bounds(x - 12, y - 12, 24, 24), "click");
            _injector.MoveTo(x, y);
            _injector.Click(button);
            return ActionResult.Injected();
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ActionPath.InputInjection, ex.Message);
        }
    }, ct);

    public ValueTask DisposeAsync()
    {
        if (_focusHandler is not null)
        {
            try { Automation.RemoveAutomationFocusChangedEventHandler(_focusHandler); } catch { }
            _focusHandler = null;
        }
        if (_overlay.IsValueCreated) _overlay.Value.Dispose();
        _registry.Clear();
        _eventGate.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- Events ----

    private async Task EnsureEventsAsync(CancellationToken ct)
    {
        if (_eventsReady) return;
        await _eventGate.WaitAsync(ct);
        try
        {
            if (_eventsReady) return;
            await Task.Run(() =>
            {
                _focusHandler = OnFocusChanged;
                Automation.AddAutomationFocusChangedEventHandler(_focusHandler);
            }, ct);
            _eventsReady = true;
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
    {
        if (sender is not AutomationElement el) return;
        try
        {
            Dispatch("focus-changed", Register(el));
        }
        catch (ElementNotAvailableException) { /* focus moved on already */ }
    }

    private void Dispatch(string kind, ElementRef source)
    {
        var evt = new AccessibilityEvent(kind, source, DateTimeOffset.Now);
        (string Kind, TaskCompletionSource<AccessibilityEvent> Tcs)[] snapshot;
        lock (_waiters) snapshot = _waiters.ToArray();
        foreach (var w in snapshot)
            if (w.Kind == kind || w.Kind.Length == 0)
                w.Tcs.TrySetResult(evt);
    }

    // ---- Element reading ----

    private AccessibleElement ReadNode(AutomationElement el, int maxDepth, CancellationToken ct, ElementRef? existingRef = null)
    {
        ct.ThrowIfCancellationRequested();
        var role = UiaRoleMap.Normalize(el.Current.ControlType);
        var isProtected = TryGet(() => el.Current.IsPassword);
        if (role == AccessibleRole.Edit && isProtected) role = AccessibleRole.PasswordEdit;
        var name = TryGet(() => el.Current.Name);
        var states = GetStates(el, role);

        // Password fields are always marked Protected and never have their text read.
        string? text = null;
        if (!isProtected && role != AccessibleRole.PasswordEdit)
            text = GetText(el, role);

        double? value = null;
        if (el.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rv))
            value = TryGetNullable(() => ((RangeValuePattern)rv).Current.Value);

        var children = GetChildren(el);
        List<AccessibleElement>? childNodes = null;
        if (maxDepth > 0 && children.Count > 0)
        {
            childNodes = new List<AccessibleElement>(children.Count);
            foreach (var child in children)
            {
                try { childNodes.Add(ReadNode(child, maxDepth - 1, ct)); }
                catch (ElementNotAvailableException) { /* child vanished mid-walk */ }
            }
        }

        return new AccessibleElement
        {
            Ref = existingRef ?? Register(el),
            Role = role,
            NativeRole = TryGet(() => el.Current.LocalizedControlType) ?? "unknown",
            Name = string.IsNullOrEmpty(name) ? null : name,
            Description = NullIfEmpty(TryGet(() => el.Current.HelpText)),
            States = states,
            Bounds = ToBounds(TryGet(() => el.Current.BoundingRectangle)),
            Text = text,
            Value = value,
            Actions = GetActions(el),
            ChildCount = children.Count,
            Children = childNodes,
        };
    }

    private static ElementState GetStates(AutomationElement el, AccessibleRole role)
    {
        var states = ElementState.None;
        var c = el.Current;
        if (c.IsEnabled) states |= ElementState.Enabled;
        if (c.IsOffscreen) states |= ElementState.Offscreen; else states |= ElementState.Visible;
        if (c.IsKeyboardFocusable) states |= ElementState.Focusable;
        if (c.HasKeyboardFocus) states |= ElementState.Focused;
        if (role == AccessibleRole.PasswordEdit || TryGetStatic(() => c.IsPassword)) states |= ElementState.Protected;

        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
        {
            states |= ElementState.Selectable;
            if (TryGetStatic(() => ((SelectionItemPattern)sel).Current.IsSelected)) states |= ElementState.Selected;
        }
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tgl) &&
            TryGetStatic(() => ((TogglePattern)tgl).Current.ToggleState == ToggleState.On))
            states |= ElementState.Checked;
        if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var exp))
        {
            var s = TryGetNullable(() => ((ExpandCollapsePattern)exp).Current.ExpandCollapseState);
            if (s is ExpandCollapseState.Expanded or ExpandCollapseState.PartiallyExpanded) states |= ElementState.Expanded;
            if (s is ExpandCollapseState.Collapsed) states |= ElementState.Collapsed;
        }
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var val))
        {
            if (TryGetStatic(() => ((ValuePattern)val).Current.IsReadOnly)) states |= ElementState.ReadOnly;
            else states |= ElementState.Editable;
        }
        else if (el.TryGetCurrentPattern(TextPattern.Pattern, out _) && role is AccessibleRole.Edit or AccessibleRole.Document)
        {
            states |= ElementState.Editable;
        }
        if (el.TryGetCurrentPattern(WindowPattern.Pattern, out var win))
        {
            if (TryGetStatic(() => ((WindowPattern)win).Current.IsModal)) states |= ElementState.Modal;
            if (TryGetStatic(() => ((WindowPattern)win).Current.IsTopmost)) states |= ElementState.Active;
        }
        return states;
    }

    private static string? GetText(AutomationElement el, AccessibleRole role)
    {
        if (role is not (AccessibleRole.Text or AccessibleRole.Edit or AccessibleRole.Label or AccessibleRole.Document))
            return null;
        // Chromium Documents report the page URL through ValuePattern; the real
        // reading text lives in TextPattern — so for Documents, text first.
        if (role != AccessibleRole.Document && el.TryGetCurrentPattern(ValuePattern.Pattern, out var v))
        {
            var s = TryGet(() => ((ValuePattern)v).Current.Value);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        if (el.TryGetCurrentPattern(TextPattern.Pattern, out var t))
        {
            var s = TryGet(() => ((TextPattern)t).DocumentRange.GetText(MaxTextLength));
            if (!string.IsNullOrEmpty(s)) return s;
        }
        if (role == AccessibleRole.Document && el.TryGetCurrentPattern(ValuePattern.Pattern, out var dv))
        {
            var s = TryGet(() => ((ValuePattern)dv).Current.Value);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    private static IReadOnlyList<string> GetActions(AutomationElement el)
    {
        var actions = new List<string>(4);
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out _)) actions.Add("invoke");
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out _)) actions.Add("toggle");
        if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _)) { actions.Add("expand"); actions.Add("collapse"); }
        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)) actions.Add("select");
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var v) && !TryGetStatic(() => ((ValuePattern)v).Current.IsReadOnly))
            actions.Add("set_text");
        if (el.TryGetCurrentPattern(RangeValuePattern.Pattern, out _)) actions.Add("set_value");
        return actions;
    }

    /// <summary>
    /// UIA reports (0,0,0,0), Infinity/NaN, or offscreen sentinels for elements that
    /// are not laid out. Guard those so agents never click a bad target — same as Linux.
    /// </summary>
    private static Bounds? ToBounds(Rect r)
    {
        if (r.IsEmpty || double.IsNaN(r.X) || double.IsNaN(r.Y) ||
            double.IsInfinity(r.X) || double.IsInfinity(r.Y) ||
            double.IsInfinity(r.Width) || double.IsInfinity(r.Height))
            return null;
        int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
        // Windows parks minimized windows at (-32000,-32000); that's "not on
        // screen", not a clickable location.
        const int Max = 100_000, MinimizedSentinel = -30_000;
        if (w <= 0 || h <= 0 || w > Max || h > Max || x < -Max || x > Max || y < -Max || y > Max
            || x <= MinimizedSentinel || y <= MinimizedSentinel)
            return null;
        return new Bounds(x, y, w, h);
    }

    // ---- Handles and plumbing ----

    private ElementRef Register(AutomationElement el)
    {
        var pid = el.Current.ProcessId; // throws ElementNotAvailable if gone
        var id = $"e{Interlocked.Increment(ref _nextId)}";
        _registry[id] = el;
        return new ElementRef(Id: id, ApplicationId: $"pid:{pid}");
    }

    private AutomationElement Resolve(ElementRef element)
    {
        if (!_registry.TryGetValue(element.Id, out var el))
            throw new StaleElementException(element);
        try
        {
            _ = el.Current.ProcessId; // liveness probe
        }
        catch (ElementNotAvailableException)
        {
            _registry.TryRemove(element.Id, out _);
            throw new StaleElementException(element);
        }
        return el;
    }

    /// <summary>Control-view children via TreeWalker, capped at <see cref="MaxChildScan"/>.</summary>
    private static List<AutomationElement> GetChildren(AutomationElement el)
    {
        var children = new List<AutomationElement>();
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var child = walker.GetFirstChild(el);
            while (child is not null && children.Count < MaxChildScan)
            {
                children.Add(child);
                child = walker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException) { /* parent vanished mid-walk */ }
        return children;
    }

    private static int ParsePid(string applicationId)
    {
        var s = applicationId.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)
            ? applicationId[4..] : applicationId;
        return int.TryParse(s, out var pid) ? pid
            : throw new ArgumentException($"Malformed application id '{applicationId}' (expected 'pid:<number>').");
    }

    private static string? ProcessName(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch { return null; }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static T? TryGet<T>(Func<T> get)
    {
        try { return get(); }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException)
        { return default; }
    }

    private static T? TryGetNullable<T>(Func<T> get) where T : struct
    {
        try { return get(); }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException)
        { return null; }
    }

    private static T TryGetStatic<T>(Func<T> get) where T : struct
    {
        try { return get(); }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException)
        { return default; }
    }

    private static bool TryGetStatic(Func<bool> get)
    {
        try { return get(); }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException)
        { return false; }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ---- DPI awareness ----

    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    /// <summary>Try to become per-monitor-v2 aware and record what actually stuck.
    /// Idempotent and safe to call more than once.</summary>
    private void EnsureDpiAwareness()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _dpiAwareness = CurrentDpiAwareness();
    }

    /// <summary>0=unaware, 1=system, 2=per-monitor, -1=unknown. Per-monitor-v1 and v2
    /// both report as 2 here; either keeps bounds aligned across mixed-scale monitors.</summary>
    private static int CurrentDpiAwareness()
    {
        try { return GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()); }
        catch { return -1; } // pre-1607 Windows without these APIs
    }

    private static string DpiAwarenessName(int a) => a switch
    {
        0 => "unaware", 1 => "system", 2 => "per-monitor", _ => "unknown"
    };

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdcDest, int xDest, int yDest, int width, int height,
        nint hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    private struct POINT { public int X, Y; }

    /// <summary>True if a pointer click at (x,y) would land on the target element's own
    /// top-level window, rather than something covering it. UIA reports occluded elements
    /// as Visible with plausible bounds, so an injected click can hit whatever is on top;
    /// this guards the injection fallback. Coarse (window-level) but fast and reliable.</summary>
    private static bool PointHitsTargetWindow(AutomationElement el, int x, int y)
    {
        try
        {
            var hwndAtPoint = WindowFromPoint(new POINT { X = x, Y = y });
            if (hwndAtPoint == 0) return false;
            // Most UIA elements are windowless; walk up the control view until an
            // ancestor exposes a real HWND, then compare root windows.
            nint targetHwnd = 0;
            var node = el;
            while (node is not null && targetHwnd == 0)
            {
                var handle = node; // capture for the lambda
                targetHwnd = new nint(TryGetStatic(() => handle.Current.NativeWindowHandle));
                if (targetHwnd == 0) node = TryGet(() => TreeWalker.ControlViewWalker.GetParent(handle));
            }
            if (targetHwnd == 0) return true; // can't resolve a window; don't block
            return GetAncestor(hwndAtPoint, GA_ROOT) == GetAncestor(targetHwnd, GA_ROOT);
        }
        catch { return true; } // never let the guard itself block a legitimate click
    }

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);
}
