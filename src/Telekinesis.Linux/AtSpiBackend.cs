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

    public Task<IReadOnlyList<AccessibleElement>> FindElementsAsync(ElementQuery query, CancellationToken ct = default)
        => throw new NotImplementedException(
            "TODO: breadth-first walk with role/name filters; bounded by query.MaxResults.");

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

    private async Task<AccessibleElement> ReadNodeAsync(string service, string path, int maxDepth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var name = await GetAccessiblePropertyStringAsync(service, path, "Name");
        var roleName = await GetRoleNameAsync(service, path);
        var children = await GetChildrenAsync(service, path);

        List<AccessibleElement>? childNodes = null;
        if (maxDepth > 0 && children.Count > 0)
        {
            childNodes = new List<AccessibleElement>(children.Count);
            foreach (var (childSvc, childPath) in children)
                childNodes.Add(await ReadNodeAsync(childSvc, childPath, maxDepth - 1, ct));
        }

        var role = AtSpiRoleMap.Normalize(roleName);
        return new AccessibleElement
        {
            Ref = EncodeRef(service, path),
            Role = role,
            NativeRole = roleName,
            Name = string.IsNullOrEmpty(name) ? null : name,
            // Protected content (password fields) is never read.
            Text = role == AccessibleRole.PasswordEdit ? null : null, // TODO: org.a11y.atspi.Text
            States = role == AccessibleRole.PasswordEdit ? ElementState.Protected : ElementState.None, // TODO: GetState
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
