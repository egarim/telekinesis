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

// telekinesis <verb> … → stateless one-shot CLI: JSON to stdout, exit code = status.
// Shell-only automation (SSH, cron, CI) with no MCP client — see docs/HEADLESS-CLI.md.
// Dispatched on the FIRST arg, and before the Contains() checks below, so operands
// that happen to say "probe" etc. are never hijacked.
if (OneShot.CanHandle(args.FirstOrDefault()))
    return await OneShot.RunAsync(args);

if (args.Contains("probe"))
    return await Probe.RunAsync(args);

// telekinesis repl [--enable-actions] → persistent session, commands on stdin, per-command timing
if (args.Contains("repl"))
    return await Repl.RunAsync(args);

// telekinesis run <scenario.json> [--enable-actions] → scripted demo with captions, 0/1 exit
if (args.FirstOrDefault() == "run")
    return await ScenarioRunner.RunAsync(args.Skip(1).ToArray());

// telekinesis pilot "<goal>" --app pid:N [--max-steps N] [--dry-run] --enable-actions
// The local UI brain (issue #10): a small local model plans one schema-constrained
// action per step over a compact candidate list; every step is trace-logged.
if (args.FirstOrDefault() == "pilot")
{
    string? POpt(string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    var goal = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
    var app = POpt("--app");
    var dryRun = args.Contains("--dry-run");
    if (goal is null || app is null)
    {
        Console.Error.WriteLine("Usage: telekinesis pilot \"<goal>\" --app pid:N [--max-steps N] [--model name] [--dry-run] --enable-actions");
        return 2;
    }
    if (!dryRun && !args.Contains("--enable-actions"))
    {
        Console.Error.WriteLine("Refusing to act without --enable-actions (use --dry-run to plan without executing).");
        return 2;
    }
    using var brain = new Telekinesis.Pilot.OllamaBrain(POpt("--brain-url"), POpt("--model"));
    if (!await brain.ProbeAsync())
    {
        Console.Error.WriteLine($"No local brain at {brain.Name}. Start Ollama (`ollama serve`), pull the model, "
            + $"or point {Telekinesis.Pilot.OllamaBrain.UrlEnvVar} at a machine that has one.");
        return 1;
    }
    await using var pilotProvider = new BackendProvider();
    var pilotBackend = await pilotProvider.GetConnectedAsync();
    Console.WriteLine($"■ pilot: \"{goal}\" on {app} via {brain.Name}{(dryRun ? " (dry-run)" : "")}\n");
    var outcome = await Telekinesis.Pilot.PilotLoop.RunAsync(
        pilotBackend, brain, app, goal,
        maxSteps: int.TryParse(POpt("--max-steps"), out var ms) ? ms : 12,
        dryRun: dryRun, say: Console.WriteLine);
    Console.WriteLine($"\n{(outcome.Success ? "✓" : "✗")} {outcome.Reason} after {outcome.Steps} step(s). "
        + $"Trace: {outcome.TraceFile}");
    return outcome.Success ? 0 : 1;
}

// telekinesis pilot-eval <trace.jsonl> [--model name] — replay a recorded trace
// through a brain without touching the UI; report agreement + latency.
if (args.FirstOrDefault() == "pilot-eval")
{
    var traceFile = args.Skip(1).FirstOrDefault(a => a.EndsWith(".jsonl"));
    if (traceFile is null || !File.Exists(traceFile))
    {
        Console.Error.WriteLine("Usage: telekinesis pilot-eval <trace.jsonl> [--model name] [--brain-url url]");
        return 2;
    }
    string? EOpt(string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    using var evalBrain = new Telekinesis.Pilot.OllamaBrain(EOpt("--brain-url"), EOpt("--model"));
    if (!await evalBrain.ProbeAsync())
    {
        Console.Error.WriteLine($"No local brain at {evalBrain.Name}.");
        return 1;
    }
    Console.WriteLine($"■ replaying {traceFile} through {evalBrain.Name}");
    var eval = await Telekinesis.Pilot.PilotEval.ReplayAsync(traceFile, evalBrain, Console.WriteLine);
    Console.WriteLine($"\nsteps={eval.Steps} agreed={eval.Agreed} invalid={eval.Invalid} "
        + $"agreement={eval.AgreementRate:P0} latency median={eval.MedianMs} ms p95={eval.P95Ms} ms");
    return 0;
}

// telekinesis assert --role Button --name Save [--app id] [--must-be visible] [--timeout-ms 5000]
// CI contract: exit 0 when the condition holds within the timeout, 1 otherwise.
if (args.FirstOrDefault() == "assert")
{
    // Same wrong-session trap as the one-shot verbs (docs/HEADLESS-CLI.md).
    if (WindowsSession.NeedsRelay())
    {
        Console.Error.WriteLine("[telekinesis] non-interactive session detected — relaying via the console session.");
        return await OneShot.RelayAsync(args);
    }
    string? Opt(string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    await using var assertProvider = new BackendProvider();
    try
    {
        var backend = await assertProvider.GetConnectedAsync();
        var result = await Telekinesis.Cli.AssertTools.RunAsync(
            backend, Opt("--role"), Opt("--name"), Opt("--app"), Opt("--must-be"),
            int.TryParse(Opt("--timeout-ms"), out var t) ? t : 3000);
        Console.WriteLine(result.Ok
            ? $"ok: [{result.Matched!.Role}] \"{result.Matched.Name}\" after {result.WaitedMs} ms"
            : $"FAILED: no match within {result.WaitedMs} ms");
        return result.Ok ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"assert failed: {ex.Message}");
        return 1;
    }
}

// telekinesis serve --sse [--port N] [--enable-actions] → HTTP/SSE transport.
// Remote posture is read-only by default: action tools require the explicit flag.
// Binds localhost only — deploy behind an authenticated tunnel (docs/REMOTE.md).
if (args.FirstOrDefault() == "serve")
{
    var portIdx = Array.IndexOf(args, "--port");
    var port = portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out var p) ? p : 3001;
    var enableActions = args.Contains("--enable-actions");

    var web = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
    web.Logging.ClearProviders();
    web.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    web.Services.AddSingleton<BackendProvider>();
    web.Services.AddSingleton<VisionMemoryService>();
    var sse = web.Services.AddMcpServer().WithHttpTransport()
        .WithTools([typeof(PerceptionTools), typeof(AssertTools), .. ProviderRegistry.Default.TrustedToolTypes]);
    if (enableActions)
        sse.WithTools([typeof(ActionTools), typeof(CredentialTools), .. ProviderRegistry.Default.ExternalToolTypes]);

    var app = web.Build();
    Microsoft.AspNetCore.Builder.McpEndpointRouteBuilderExtensions.MapMcp(app);
    Console.Error.WriteLine($"[telekinesis] serving MCP over HTTP on http://127.0.0.1:{port} "
        + (enableActions ? "(actions ENABLED)" : "(read-only — start with --enable-actions to allow actions)"));
    Console.Error.WriteLine($"[telekinesis] audit log: {Telekinesis.Cli.AuditLog.Path}");
    await app.RunAsync($"http://127.0.0.1:{port}");
    return 0;
}

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
    // Provider plugins: which are loaded, and whether any came from outside the
    // tree. External assemblies run with the server's full power — flag them.
    foreach (var entry in Telekinesis.Cli.ProviderRegistry.Default.Entries)
        Console.WriteLine(entry.External
            ? $"  [!!] provider: {entry.Plugin.Name} (priority {entry.Plugin.Priority}) — EXTERNAL, unsigned, loaded from {entry.Origin}. It has the same power as the server itself."
            : $"  [ok] provider: {entry.Plugin.Name} (priority {entry.Plugin.Priority}, built-in).");
    foreach (var warning in Telekinesis.Cli.ProviderRegistry.Default.LoadWarnings)
        Console.WriteLine($"  [--] provider: {warning}");

    // Browsers publish their pages into the tree only once renderer accessibility
    // is active (Chromium activates it lazily). Report each running browser.
    try
    {
        var connected = await provider.GetConnectedAsync();
        foreach (var app in await connected.ListApplicationsAsync())
        {
            if (app.ProcessId is not { } pid) continue;
            string name;
            try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName.ToLowerInvariant(); }
            catch { continue; }
            if (name is not ("msedge" or "chrome" or "chromium" or "firefox" or "brave" or "vivaldi" or "opera")) continue;

            var doc = await Telekinesis.Cli.BrowserPages.FindDocumentAsync(connected, app.Id, titleContains: null, default);
            var realized = false;
            if (doc is not null)
            {
                var node = await connected.ReadElementAsync(doc.Ref);
                realized = node.ChildCount > 0;
            }
            Console.WriteLine(realized
                ? $"  [ok] browser: {name} ({app.Id}) — page accessibility active."
                : $"  [--] browser: {name} ({app.Id}) — page tree not realized. Chromium builds it lazily; read_page warms it, or relaunch with --force-renderer-accessibility.");
        }
    }
    catch { /* browser check is best-effort; never blocks readiness */ }

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
    .WithTools([typeof(PerceptionTools), typeof(AssertTools), .. ProviderRegistry.Default.TrustedToolTypes]);

if (!readOnly)
    mcp.WithTools([typeof(ActionTools), typeof(CredentialTools), .. ProviderRegistry.Default.ExternalToolTypes]);

await builder.Build().RunAsync();
return 0;
