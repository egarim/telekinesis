using Tmds.DBus.Protocol;
using Telekinesis.Abstractions;

namespace Telekinesis.Linux;

/// <summary>
/// Linux backend: an AT-SPI client speaking the org.a11y.atspi.* protocol
/// directly over D-Bus via Tmds.DBus.Protocol (pure managed, AOT-friendly).
///
/// Perception is implemented against the accessibility bus. Actions use AT-SPI
/// Action/EditableText where available and will fall back to uinput injection
/// (see InputInjector) — the injection path is the next milestone.
/// </summary>
public sealed class AtSpiBackend : IAccessibilityBackend
{
    private const string RegistryService = "org.a11y.atspi.Registry";
    private const string RootPath = "/org/a11y/atspi/accessible/root";
    private const string AccessibleIface = "org.a11y.atspi.Accessible";

    private DBusConnection? _a11yConnection;
    private UinputInjector? _injector;
    private readonly SemaphoreSlim _injectorGate = new(1, 1);

    // Event tracking (focus + generic state changes).
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly List<(string Kind, TaskCompletionSource<AccessibilityEvent> Tcs)> _waiters = new();
    private readonly List<IDisposable> _subscriptions = new();
    private volatile ElementRef? _lastFocused;
    private bool _eventsReady;

    public string Name => "AT-SPI (Linux)";

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "The AT-SPI backend requires Linux with a D-Bus session and the accessibility bus enabled.");

        // Bound the whole connect sequence so a missing or wedged bus fails cleanly
        // instead of hanging the caller (Tmds connect has no timeout of its own).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        var tk = timeoutCts.Token;

        try
        {
            // The a11y bus is a separate bus whose address is published on the session bus.
            using var session = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException(
                "DBUS_SESSION_BUS_ADDRESS is not set; no D-Bus session available."));
            await session.ConnectAsync().AsTask().WaitAsync(tk);

            var address = StripGuid(await GetA11yBusAddressAsync(session).WaitAsync(tk));
            _a11yConnection = new DBusConnection(address);
            await _a11yConnection.ConnectAsync().AsTask().WaitAsync(tk);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Timed out connecting to the accessibility bus. Check that it is running and enabled (telekinesis doctor).");
        }
    }

    /// <summary>
    /// Removes the <c>guid=…</c> parameter from a D-Bus address. org.a11y.Bus.GetAddress
    /// can return a stale guid when the a11y bus has been restarted (common with
    /// containerised desktops), and Tmds validates it strictly, failing with
    /// "Unexpected GUID". Without a pinned guid we accept the live daemon on the socket.
    /// </summary>
    private static string StripGuid(string address)
    {
        var entries = address.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var parts = entries[i].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("guid=", StringComparison.OrdinalIgnoreCase));
            entries[i] = string.Join(',', parts);
        }
        return string.Join(';', entries);
    }

    public async Task<DiagnosticReport> DiagnoseAsync(CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();

        if (!OperatingSystem.IsLinux())
        {
            items.Add(new("platform", false, "Not running on Linux.",
                "Use the Windows (UIA) or macOS (AXAPI) backend on this OS."));
            return new DiagnosticReport(false, items);
        }

        items.Add(new("dbus-session", DBusAddress.Session is not null,
            DBusAddress.Session is not null ? "D-Bus session bus address found." : "DBUS_SESSION_BUS_ADDRESS is not set.",
            DBusAddress.Session is null ? "Run inside a desktop session, or export DBUS_SESSION_BUS_ADDRESS." : null));

        try
        {
            if (_a11yConnection is null) await ConnectAsync(ct);
            var apps = await ListApplicationsAsync(ct);
            items.Add(new("a11y-bus", true, $"Accessibility bus reachable; {apps.Count} application(s) registered."));

            // Even with the bus up, toolkits (GTK/Qt) only register their trees when
            // accessibility is *enabled*. If it's off, apps are invisible — the most
            // common "0 applications" cause.
            var enabled = await GetA11yEnabledAsync();
            items.Add(new("a11y-enabled", enabled != false,
                enabled switch
                {
                    true => "Accessibility is enabled; toolkits will expose their trees.",
                    false => "Accessibility is DISABLED — GTK/Qt apps will not register (you'll see 0 applications).",
                    null => "Could not read org.a11y.Status.IsEnabled (assuming enabled).",
                },
                enabled == false
                    ? "Enable it: gsettings set org.gnome.desktop.interface toolkit-accessibility true "
                      + "(Electron/Chromium also need --force-renderer-accessibility)."
                    : null));
        }
        catch (Exception ex)
        {
            items.Add(new("a11y-bus", false, $"Cannot reach the accessibility bus: {ex.Message}",
                "Enable it: gsettings set org.gnome.desktop.interface toolkit-accessibility true "
                + "(and note Electron/Chromium apps need --force-renderer-accessibility to appear)."));
        }

        var uinputOk = File.Exists("/dev/uinput") && CanOpenUinput();
        items.Add(new("uinput", uinputOk,
            uinputOk ? "/dev/uinput is accessible; input injection available."
                     : "/dev/uinput is missing or not writable; actions limited to native AT-SPI paths.",
            uinputOk ? null : "Add a udev rule granting your user access (telekinesis setup prints it)."));

        return new DiagnosticReport(items.All(i => i.Ok || i.Check == "uinput"), items);
    }

    // ---- Perception ----

    public async Task<IReadOnlyList<ApplicationInfo>> ListApplicationsAsync(CancellationToken ct = default)
    {
        var children = await GetChildrenAsync(RegistryService, RootPath);
        var apps = new List<ApplicationInfo>(children.Count);
        foreach (var (service, path) in children)
        {
            var name = await GetAccessiblePropertyStringAsync(service, path, "Name");
            apps.Add(new ApplicationInfo(Id: service, Name: name ?? service, ProcessId: null));
        }
        return apps;
    }

    public async Task<AccessibleElement> GetTreeAsync(string applicationId, int maxDepth = 3, CancellationToken ct = default)
    {
        // Application roots live at the well-known root path within each app's connection.
        return await ReadNodeAsync(applicationId, RootPath, maxDepth, ct);
    }

    public async Task<IReadOnlyList<AccessibleElement>> FindElementsAsync(ElementQuery query, CancellationToken ct = default)
    {
        // Breadth-first over the accessible tree, bounded by MaxResults and a node
        // cap so a runaway tree can never hang the call. Matched nodes are returned
        // flat (no children populated) — callers drill down with get_tree/read_element.
        const int NodeCap = 20_000;
        var results = new List<AccessibleElement>();
        var visitedNodes = 0;

        // Seed with the requested app, or every application on the bus.
        var queue = new Queue<(string Service, string Path)>();
        if (!string.IsNullOrEmpty(query.ApplicationId))
            queue.Enqueue((query.ApplicationId, RootPath));
        else
            foreach (var app in await ListApplicationsAsync(ct))
                queue.Enqueue((app.Id, RootPath));

        while (queue.Count > 0 && results.Count < query.MaxResults && visitedNodes < NodeCap)
        {
            ct.ThrowIfCancellationRequested();
            var (service, path) = queue.Dequeue();
            visitedNodes++;

            var roleName = await GetRoleNameAsync(service, path);
            var role = AtSpiRoleMap.Normalize(roleName);
            var name = await GetAccessiblePropertyStringAsync(service, path, "Name");

            if (Matches(query, role, name))
            {
                var states = await GetStateAsync(service, path);
                if (role == AccessibleRole.PasswordEdit) states |= ElementState.Protected;
                if (query.WithStates is { } required && (states & required) != required)
                {
                    // filtered out by state; still descend into children below
                }
                else
                {
                    results.Add(new AccessibleElement
                    {
                        Ref = EncodeRef(service, path),
                        Role = role,
                        NativeRole = roleName,
                        Name = string.IsNullOrEmpty(name) ? null : name,
                        States = states,
                        Bounds = await GetExtentsAsync(service, path),
                        ChildCount = 0,
                    });
                }
            }

            foreach (var child in await GetChildrenAsync(service, path))
                queue.Enqueue(child);
        }

        return results;
    }

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

    public async Task<AccessibleElement> ReadElementAsync(ElementRef element, CancellationToken ct = default)
    {
        var (service, path) = DecodeRef(element);
        try
        {
            return await ReadNodeAsync(service, path, maxDepth: 0, ct);
        }
        catch (DBusExceptionBase)
        {
            throw new StaleElementException(element);
        }
    }

    public async Task<AccessibleElement?> GetFocusedAsync(CancellationToken ct = default)
    {
        await EnsureEventsAsync(ct);
        var focused = _lastFocused;
        if (focused is null) return null;
        try
        {
            return await ReadElementAsync(focused, ct);
        }
        catch (StaleElementException)
        {
            return null;
        }
    }

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

    public async Task<ActionResult> InvokeAsync(ElementRef element, string? action = null, CancellationToken ct = default)
    {
        var (service, path) = DecodeRef(element);
        // Try the native accessibility action first (index 0 = the default action).
        if (await DoActionAsync(service, path, 0))
            return ActionResult.Native();
        // Fall back to a pointer click at the element's center.
        return await ClickAsync(element, PointerButton.Left, ct);
    }

    public async Task<ActionResult> SetTextAsync(ElementRef element, string text, CancellationToken ct = default)
    {
        var (service, path) = DecodeRef(element);
        if (await SetEditableTextAsync(service, path, text))
            return ActionResult.Native();
        // Fallback: focus via click, select all, type over the selection.
        var click = await ClickAsync(element, PointerButton.Left, ct);
        if (!click.Success) return click;
        var inj = EnsureInjector();
        inj.Chord([LinuxKeyMap.KEY_LEFTCTRL, LinuxKeyMap.KEY_A]);
        inj.TypeText(text);
        return ActionResult.Injected();
    }

    public async Task<ActionResult> SetValueAsync(ElementRef element, double value, CancellationToken ct = default)
    {
        var (service, path) = DecodeRef(element);
        if (await SetValuePropertyAsync(service, path, value))
            return ActionResult.Native();
        return ActionResult.Failed(ActionPath.NativeAction,
            "Element does not expose the AT-SPI Value interface.");
    }

    public async Task<ActionResult> ClickAsync(ElementRef element, PointerButton button = PointerButton.Left, CancellationToken ct = default)
    {
        var (service, path) = DecodeRef(element);
        var bounds = await GetExtentsAsync(service, path);
        if (bounds is null || bounds.Width <= 0 || bounds.Height <= 0)
            return ActionResult.Failed(ActionPath.InputInjection,
                "Element has no on-screen bounds to click.");
        try
        {
            var inj = await EnsureInjectorAsync(ct);
            inj.MoveTo(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            inj.Click(ToBtn(button));
            return ActionResult.Injected();
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ActionPath.InputInjection, ex.Message);
        }
    }

    public async Task<ActionResult> TypeTextAsync(string text, CancellationToken ct = default)
    {
        try
        {
            (await EnsureInjectorAsync(ct)).TypeText(text);
            return ActionResult.Injected();
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ActionPath.InputInjection, ex.Message);
        }
    }

    public async Task<ActionResult> PressKeysAsync(string combination, CancellationToken ct = default)
    {
        var codes = new List<int>();
        foreach (var part in combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LinuxKeyMap.TryNamedKey(part, out var named)) codes.Add(named);
            else if (part.Length == 1 && LinuxKeyMap.TryChar(part[0], out var code, out _)) codes.Add(code);
            else return ActionResult.Failed(ActionPath.InputInjection, $"Unknown key '{part}'.");
        }
        if (codes.Count == 0)
            return ActionResult.Failed(ActionPath.InputInjection, "Empty key combination.");
        try
        {
            (await EnsureInjectorAsync(ct)).Chord(codes);
            return ActionResult.Injected();
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ActionPath.InputInjection, ex.Message);
        }
    }

    private static int ToBtn(PointerButton b) => b switch
    {
        PointerButton.Right => LinuxKeyMap.BTN_RIGHT,
        PointerButton.Middle => LinuxKeyMap.BTN_MIDDLE,
        _ => LinuxKeyMap.BTN_LEFT,
    };

    public ValueTask DisposeAsync()
    {
        foreach (var sub in _subscriptions) sub.Dispose();
        _injector?.Dispose();
        _injectorGate.Dispose();
        _eventGate.Dispose();
        _a11yConnection?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- Injector lifecycle ----

    private UinputInjector EnsureInjector() =>
        _injector ?? throw new InvalidOperationException("Injector not initialized; call EnsureInjectorAsync.");

    /// <summary>Lazily creates the virtual input device, sized to the desktop bounds.</summary>
    private async Task<UinputInjector> EnsureInjectorAsync(CancellationToken ct)
    {
        if (_injector is not null) return _injector;
        await _injectorGate.WaitAsync(ct);
        try
        {
            if (_injector is null)
            {
                // The desktop root's extents give the screen size for ABS mapping.
                var desktop = await GetExtentsAsync(RegistryService, RootPath);
                var w = desktop is { Width: > 0 } ? desktop.Width : 1920;
                var h = desktop is { Height: > 0 } ? desktop.Height : 1080;
                _injector = new UinputInjector(w, h);
            }
            return _injector;
        }
        finally
        {
            _injectorGate.Release();
        }
    }

    // ---- Event subscription ----

    private const string EventObjectIface = "org.a11y.atspi.Event.Object";

    /// <summary>
    /// One-time setup: register interest with the AT-SPI registry and start
    /// watching object state-changed signals to track focus and feed waiters.
    /// </summary>
    private async Task EnsureEventsAsync(CancellationToken ct)
    {
        if (_eventsReady) return;
        await _eventGate.WaitAsync(ct);
        try
        {
            if (_eventsReady) return;

            // Ask the registry to route the events we care about to us.
            await RegisterEventAsync("object:state-changed:focused");

            // Watch StateChanged from any sender/path; filter by minor client-side.
            // Body signature is "siiv(so)": minor, detail1, detail2, any_data, (source).
            var sub = await Bus.WatchSignalAsync(
                null!, EventObjectIface, null!, "StateChanged",
                (Message m, object? _) =>
                {
                    var reader = m.GetBodyReader();
                    var minor = reader.ReadString();
                    var detail1 = reader.ReadInt32();
                    reader.ReadInt32();               // detail2 (unused)
                    reader.ReadVariantValue();        // any_data (skip)
                    reader.AlignStruct();
                    var srcService = reader.ReadString();
                    var srcPath = reader.ReadObjectPathAsString();
                    return new RawStateEvent(minor, detail1, srcService, srcPath);
                },
                (Notification<RawStateEvent> n) =>
                {
                    if (n.Exception is not null || !n.HasValue) return;
                    OnStateChanged(n.Value);
                },
                ObserverFlags.None, true, null!);

            _subscriptions.Add(sub);
            _eventsReady = true;
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private readonly record struct RawStateEvent(string Minor, int Detail1, string SrcService, string SrcPath);

    private void OnStateChanged(RawStateEvent e)
    {
        // Focus gained: minor "focused", detail1 == 1.
        if (e.Minor == "focused" && e.Detail1 == 1)
        {
            var reference = EncodeRef(e.SrcService, e.SrcPath);
            _lastFocused = reference;
            Dispatch("focus-changed", reference);
        }
        Dispatch($"state-changed:{e.Minor}", EncodeRef(e.SrcService, e.SrcPath));
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

    /// <summary>org.a11y.atspi.Registry.RegisterEvent(s eventName).</summary>
    private async Task RegisterEventAsync(string eventName)
    {
        MessageBuffer CreateMessage()
        {
            using var writer = Bus.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: RegistryService, path: "/org/a11y/atspi/registry",
                @interface: "org.a11y.atspi.Registry", member: "RegisterEvent", signature: "s");
            writer.WriteString(eventName);
            return writer.CreateMessage();
        }
        try { await Bus.CallMethodAsync(CreateMessage()); }
        catch (DBusExceptionBase) { /* registry may auto-route; watching still works */ }
    }

    // ---- Native action D-Bus helpers ----

    /// <summary>org.a11y.atspi.Action.DoAction(i index) → b. False if no Action interface.</summary>
    private async Task<bool> DoActionAsync(string service, string path, int index)
    {
        MessageBuffer CreateMessage()
        {
            using var writer = Bus.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: service, path: path,
                @interface: "org.a11y.atspi.Action", member: "DoAction", signature: "i");
            writer.WriteInt32(index);
            return writer.CreateMessage();
        }
        try
        {
            return await Bus.CallMethodAsync(CreateMessage(),
                static (Message m, object? _) => m.GetBodyReader().ReadBool(), null);
        }
        catch (DBusExceptionBase)
        {
            return false;
        }
    }

    /// <summary>org.a11y.atspi.EditableText.SetTextContents(s) → b.</summary>
    private async Task<bool> SetEditableTextAsync(string service, string path, string text)
    {
        MessageBuffer CreateMessage()
        {
            using var writer = Bus.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: service, path: path,
                @interface: "org.a11y.atspi.EditableText", member: "SetTextContents", signature: "s");
            writer.WriteString(text);
            return writer.CreateMessage();
        }
        try
        {
            return await Bus.CallMethodAsync(CreateMessage(),
                static (Message m, object? _) => m.GetBodyReader().ReadBool(), null);
        }
        catch (DBusExceptionBase)
        {
            return false;
        }
    }

    /// <summary>Set org.a11y.atspi.Value.CurrentValue (d) via Properties.Set.</summary>
    private async Task<bool> SetValuePropertyAsync(string service, string path, double value)
    {
        MessageBuffer CreateMessage()
        {
            using var writer = Bus.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: service, path: path,
                @interface: "org.freedesktop.DBus.Properties", member: "Set", signature: "ssv");
            writer.WriteString("org.a11y.atspi.Value");
            writer.WriteString("CurrentValue");
            writer.WriteVariantDouble(value);
            return writer.CreateMessage();
        }
        try
        {
            await Bus.CallMethodAsync(CreateMessage());
            return true;
        }
        catch (DBusExceptionBase)
        {
            return false;
        }
    }

    // ---- D-Bus plumbing ----

    private DBusConnection Bus => _a11yConnection
        ?? throw new InvalidOperationException("Not connected; call ConnectAsync first.");

    /// <summary>Reads org.a11y.Status.IsEnabled from the session bus. Null if unreadable.</summary>
    private static async Task<bool?> GetA11yEnabledAsync()
    {
        try
        {
            using var session = new DBusConnection(DBusAddress.Session!);
            await session.ConnectAsync();
            MessageBuffer CreateMessage()
            {
                using var writer = session.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: "org.a11y.Bus", path: "/org/a11y/bus",
                    @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
                writer.WriteString("org.a11y.Status");
                writer.WriteString("IsEnabled");
                return writer.CreateMessage();
            }
            return await session.CallMethodAsync(CreateMessage(),
                static (Message m, object? _) => (bool?)m.GetBodyReader().ReadVariantValue().GetBool(), null);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> GetA11yBusAddressAsync(DBusConnection session)
    {
        // MessageWriter is a ref struct: build the buffer before any await.
        MessageBuffer CreateMessage()
        {
            using var writer = session.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.a11y.Bus", path: "/org/a11y/bus",
                @interface: "org.a11y.Bus", member: "GetAddress");
            return writer.CreateMessage();
        }
        return await session.CallMethodAsync(CreateMessage(),
            static (Message m, object? _) => m.GetBodyReader().ReadString(), null);
    }

    /// <summary>Reads an AT-SPI a(so) child list.</summary>
    private async Task<IReadOnlyList<(string Service, string Path)>> GetChildrenAsync(string service, string path)
    {
        MessageBuffer CreateMessage()
        {
            using var writer = Bus.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: service, path: path,
                @interface: AccessibleIface, member: "GetChildren");
            return writer.CreateMessage();
        }
        return await Bus.CallMethodAsync(CreateMessage(),
            static (Message m, object? _) =>
            {
                var reader = m.GetBodyReader();
                var list = new List<(string, string)>();
                var end = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(end))
                {
                    var svc = reader.ReadString();
                    var objPath = reader.ReadObjectPathAsString();
                    list.Add((svc, objPath));
                }
                return (IReadOnlyList<(string, string)>)list;
            }, null);
    }

    private async Task<string?> GetAccessiblePropertyStringAsync(string service, string path, string property)
    {
        MessageBuffer CreateMessage()
        {
            using var writer = Bus.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: service, path: path,
                @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
            writer.WriteString(AccessibleIface);
            writer.WriteString(property);
            return writer.CreateMessage();
        }
        try
        {
            return await Bus.CallMethodAsync(CreateMessage(),
                static (Message m, object? _) => m.GetBodyReader().ReadVariantValue().GetString(), null);
        }
        catch (DBusExceptionBase)
        {
            return null;
        }
    }

    private async Task<string> GetRoleNameAsync(string service, string path)
    {
        MessageBuffer CreateMessage()
        {
            using var writer = Bus.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: service, path: path,
                @interface: AccessibleIface, member: "GetRoleName");
            return writer.CreateMessage();
        }
        return await Bus.CallMethodAsync(CreateMessage(),
            static (Message m, object? _) => m.GetBodyReader().ReadString(), null);
    }

    private const string ComponentIface = "org.a11y.atspi.Component";
    private const string TextIface = "org.a11y.atspi.Text";

    /// <summary>org.a11y.atspi.Accessible.GetState → au (two uint32 state words).</summary>
    private async Task<ElementState> GetStateAsync(string service, string path)
    {
        MessageBuffer CreateMessage()
        {
            using var writer = Bus.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: service, path: path,
                @interface: AccessibleIface, member: "GetState");
            return writer.CreateMessage();
        }
        try
        {
            var words = await Bus.CallMethodAsync(CreateMessage(),
                static (Message m, object? _) => m.GetBodyReader().ReadArrayOfUInt32(), null);
            return AtSpiStateMap.Map(words);
        }
        catch (DBusExceptionBase)
        {
            return ElementState.None;
        }
    }

    /// <summary>org.a11y.atspi.Component.GetExtents(u coordType) → (iiii). coordType 0 = screen.</summary>
    private async Task<Bounds?> GetExtentsAsync(string service, string path)
    {
        MessageBuffer CreateMessage()
        {
            using var writer = Bus.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: service, path: path,
                @interface: ComponentIface, member: "GetExtents", signature: "u");
            writer.WriteUInt32(0u); // ATSPI_COORD_TYPE_SCREEN
            return writer.CreateMessage();
        }
        try
        {
            return await Bus.CallMethodAsync(CreateMessage(),
                static (Message m, object? _) =>
                {
                    var reader = m.GetBodyReader();
                    reader.AlignStruct();
                    int x = reader.ReadInt32(), y = reader.ReadInt32();
                    int w = reader.ReadInt32(), h = reader.ReadInt32();
                    // AT-SPI returns sentinel/garbage extents for widgets that are not
                    // laid out (e.g. off-screen notebook pages): huge or negative sizes.
                    // Treat those as "no usable bounds" so agents never click a bad target.
                    // Compare without Math.Abs: Math.Abs(int.MinValue) throws, and some
                    // toolkits use int.MinValue as an "unplaced" sentinel coordinate.
                    const int Max = 100_000;
                    if (w <= 0 || h <= 0 || w > Max || h > Max || x < -Max || x > Max || y < -Max || y > Max)
                        return (Bounds?)null;
                    return (Bounds?)new Bounds(x, y, w, h);
                }, null);
        }
        catch (DBusExceptionBase)
        {
            return null; // element has no Component interface
        }
    }

    /// <summary>org.a11y.atspi.Text.GetText(start, end) → s. Never called for protected fields.</summary>
    private async Task<string?> GetTextAsync(string service, string path)
    {
        MessageBuffer CreateMessage()
        {
            using var writer = Bus.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: service, path: path,
                @interface: TextIface, member: "GetText", signature: "ii");
            writer.WriteInt32(0);
            writer.WriteInt32(-1); // -1 = to the end
            return writer.CreateMessage();
        }
        try
        {
            var text = await Bus.CallMethodAsync(CreateMessage(),
                static (Message m, object? _) => m.GetBodyReader().ReadString(), null);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (DBusExceptionBase)
        {
            return null; // element has no Text interface
        }
    }

    private async Task<AccessibleElement> ReadNodeAsync(string service, string path, int maxDepth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var name = await GetAccessiblePropertyStringAsync(service, path, "Name");
        var roleName = await GetRoleNameAsync(service, path);
        var role = AtSpiRoleMap.Normalize(roleName);
        var states = await GetStateAsync(service, path);
        var bounds = await GetExtentsAsync(service, path);
        var children = await GetChildrenAsync(service, path);

        // Password fields are always marked Protected and never have their text read.
        var isProtected = role == AccessibleRole.PasswordEdit;
        if (isProtected) states |= ElementState.Protected;
        string? text = null;
        if (!isProtected && role is AccessibleRole.Text or AccessibleRole.Edit or AccessibleRole.Label or AccessibleRole.Document)
            text = await GetTextAsync(service, path);

        List<AccessibleElement>? childNodes = null;
        if (maxDepth > 0 && children.Count > 0)
        {
            childNodes = new List<AccessibleElement>(children.Count);
            foreach (var (childSvc, childPath) in children)
                childNodes.Add(await ReadNodeAsync(childSvc, childPath, maxDepth - 1, ct));
        }

        return new AccessibleElement
        {
            Ref = EncodeRef(service, path),
            Role = role,
            NativeRole = roleName,
            Name = string.IsNullOrEmpty(name) ? null : name,
            States = states,
            Bounds = bounds,
            Text = text,
            ChildCount = children.Count,
            Children = childNodes,
        };
    }

    private static ElementRef EncodeRef(string service, string path) =>
        new(Id: $"{service}|{path}", ApplicationId: service);

    private static (string Service, string Path) DecodeRef(ElementRef element)
    {
        var parts = element.Id.Split('|', 2);
        if (parts.Length != 2)
            throw new ArgumentException($"Malformed element id '{element.Id}'.");
        return (parts[0], parts[1]);
    }

    private static bool CanOpenUinput()
    {
        try
        {
            using var fs = File.Open("/dev/uinput", FileMode.Open, FileAccess.Write);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
