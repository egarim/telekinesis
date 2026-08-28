using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telekinesis.Cli;

// telekinesis            → run the MCP server on stdio (full mode)
// telekinesis --read-only → MCP server, perception tools only ("clairvoyant mode")
// telekinesis doctor     → diagnose the environment and exit
// telekinesis setup      → print the platform setup steps (udev rule, TCC, ...) and exit
// telekinesis probe ...  → exercise the backend from the terminal (VM validation)

#if !WINDOWS
// The dotnet-tool package can only target plain net10.0 (PackAsTool rejects the
// -windows TFM), but the UIA backend needs the desktop framework. The tool build
// therefore bundles the net10.0-windows publish under win/ and, on Windows,
// re-execs it with stdio inherited — MCP-over-stdio passes straight through.
if (OperatingSystem.IsWindows())
{
    var winPayload = Path.Combine(AppContext.BaseDirectory, "win", "Telekinesis.Cli.dll");
    if (File.Exists(winPayload))
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet") {UseShellExecute = false};
        psi.ArgumentList.Add(winPayload);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var child = System.Diagnostics.Process.Start(psi)!;
        await child.WaitForExitAsync();
        return child.ExitCode;
    }
}
#endif

if (args.Contains("probe"))
    return await Probe.RunAsync(args);

// telekinesis repl [--enable-actions] → persistent session, commands on stdin, per-command timing
if (args.Contains("repl"))
    return await Repl.RunAsync(args);

if (args.Contains("doctor"))
{
    await using var provider = new BackendProvider();
    Telekinesis.Abstractions.IAccessibilityBackend backend;
    try
    {
        backend = provider.GetOrCreateUnconnected();
    }
    catch (PlatformNotSupportedException ex)
    {
        Console.WriteLine($"Telekinesis doctor — {ex.Message}");
        return 1;
    }
    var report = await backend.DiagnoseAsync();
    Console.WriteLine($"Telekinesis doctor — backend: {backend.Name}");
    foreach (var item in report.Items)
    {
        Console.WriteLine($"  [{(item.Ok ? "ok" : "!!")}] {item.Check}: {item.Detail}");
        if (!item.Ok && item.Remedy is not null)
            Console.WriteLine($"       fix: {item.Remedy}");
    }
    // Vision tier is optional — report it, but never block readiness on it.
    using (var parser = new Telekinesis.Vision.OmniParserClient())
    {
        var visionOk = await parser.ProbeAsync();
        Console.WriteLine($"  [{(visionOk ? "ok" : "--")}] vision: OmniParser sidecar at {parser.BaseUrl} "
            + (visionOk ? "is reachable." : $"not reachable (optional; see docs/VISION.md)."));
    }
    Console.WriteLine(report.Ready ? "Ready." : "Not ready — fix the items above.");
    return report.Ready ? 0 : 1;
}

// telekinesis memory            → perceptual-memory stats
// telekinesis memory export --out <dir> → dump as a training-ready dataset
if (args.Contains("memory"))
{
    var memoryService = new VisionMemoryService();
    if (memoryService.Memory is null)
    {
        Console.Error.WriteLine("Perceptual memory is not available on this platform yet.");
        return 1;
    }
    if (args.Contains("export"))
    {
        var outIdx = Array.IndexOf(args, "--out");
        if (outIdx < 0 || outIdx + 1 >= args.Length)
        {
            Console.Error.WriteLine("Usage: telekinesis memory export --out <dir>");
            return 2;
        }
        var count = memoryService.Memory.Export(args[outIdx + 1]);
        Console.WriteLine($"Exported {count} grounded sample(s) to {args[outIdx + 1]} (dataset.jsonl + crops/).");
        return 0;
    }
    var (parses, anchors, dir) = memoryService.Memory.Stats();
    Console.WriteLine($"Perceptual memory at {dir}");
    Console.WriteLine($"  cached parses : {parses}");
    Console.WriteLine($"  anchors       : {anchors}");
    Console.WriteLine("Export as a training dataset with: telekinesis memory export --out <dir>");
    return 0;
}

if (args.Contains("setup"))
{
    Console.WriteLine(
        """
        Telekinesis setup
        =================
        Linux — input injection needs /dev/uinput access:
          sudo tee /etc/udev/rules.d/99-telekinesis-uinput.rules <<'EOF'
          KERNEL=="uinput", GROUP="input", MODE="0660"
          EOF
          sudo usermod -aG input $USER   # then log out and back in
          sudo udevadm control --reload-rules && sudo udevadm trigger

        Linux — the accessibility bus must be enabled:
          gsettings set org.gnome.desktop.interface toolkit-accessibility true
          (Electron/Chromium apps additionally need --force-renderer-accessibility)

        macOS — grant Accessibility permission to the terminal or host process in
          System Settings > Privacy & Security > Accessibility. (Backend TODO.)

        Windows — no setup usually required; run elevated to reach elevated apps. (Backend TODO.)
        """);
    return 0;
}

var readOnly = args.Contains("--read-only");

var builder = Host.CreateApplicationBuilder(args);
// stdout carries the MCP protocol — all logging must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<BackendProvider>();
builder.Services.AddSingleton<VisionMemoryService>();

var mcp = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools([typeof(PerceptionTools)]);

if (!readOnly)
    mcp.WithTools([typeof(ActionTools)]);

await builder.Build().RunAsync();
return 0;
