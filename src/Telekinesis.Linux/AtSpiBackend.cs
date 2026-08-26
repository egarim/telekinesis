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

    public string Name => "AT-SPI (Linux)";

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "The AT-SPI backend requires Linux with a D-Bus session and the accessibility bus enabled.");

        // The a11y bus is a separate bus whose address is published on the session bus.
        using var session = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException(
            "DBUS_SESSION_BUS_ADDRESS is not set; no D-Bus session available."));
        await session.ConnectAsync();

        var address = await GetA11yBusAddressAsync(session);
        _a11yConnection = new DBusConnection(address);
        await _a11yConnection.ConnectAsync();
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
        }
        catch (Exception ex)
        {
            items.Add(new("a11y-bus", false, $"Cannot reach the accessibility bus: {ex.Message}",
                "Enable it: gsettings set org.gnome.desktop.interface toolkit-accessibility true " +
                "(and note Electron/Chromium apps need --force-renderer-accessibility to appear)."));
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

    public Task<AccessibleElement?> GetFocusedAsync(CancellationToken ct = default)
        => throw new NotImplementedException(
            "TODO: track state-changed:focused signals; requires RegisterEvent on the registry.");

    public Task<AccessibilityEvent?> WaitForEventAsync(string kind, TimeSpan timeout, CancellationToken ct = default)
        => throw new NotImplementedException(
            "TODO: subscribe to org.a11y.atspi.Event.* signals and surface them as AccessibilityEvents.");

    // ---- Actions ----

    public Task<ActionResult> InvokeAsync(ElementRef element, string? action = null, CancellationToken ct = default)
        => throw new NotImplementedException(
            "TODO: org.a11y.atspi.Action.DoAction, falling back to ClickAsync via uinput.");

    public Task<ActionResult> SetTextAsync(ElementRef element, string text, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: org.a11y.atspi.EditableText.SetTextContents, fallback focus+type.");

    public Task<ActionResult> SetValueAsync(ElementRef element, double value, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: org.a11y.atspi.Value CurrentValue property.");

    public Task<ActionResult> ClickAsync(ElementRef element, PointerButton button = PointerButton.Left, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: resolve bounds via Component.GetExtents, inject via uinput.");

    public Task<ActionResult> TypeTextAsync(string text, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: uinput key events.");

    public Task<ActionResult> PressKeysAsync(string combination, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: uinput key combination.");

    public ValueTask DisposeAsync()
    {
        _a11yConnection?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- D-Bus plumbing ----

    private DBusConnection Bus => _a11yConnection
        ?? throw new InvalidOperationException("Not connected; call ConnectAsync first.");

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
