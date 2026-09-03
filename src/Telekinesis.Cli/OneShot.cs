using System.Text.Json;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// `telekinesis &lt;verb&gt;` — stateless one-shot CLI for shell-only automation (issue #37):
/// every perception/action as a single process that prints JSON to stdout and exits
/// (0 ok, 1 failed, 2 usage/refused). No MCP client, no session — SSH-friendly.
///
///   perception: apps | tree [--app X] [--depth N] | find "&lt;query&gt;" | read "&lt;query&gt;"
///               focused | snapshot [--app X]
///   actions   : click "&lt;query&gt;" | click-at X Y | invoke "&lt;query&gt;" [--action expand]
///               set-text "&lt;query&gt;" "&lt;text&gt;" | type "&lt;text&gt;" | press "&lt;keys&gt;"
///               launch &lt;exe&gt; [args…]          (all gated behind --enable-actions)
///
/// Element refs cannot survive across processes, so targets are queries re-resolved
/// per call: `"Save"` (name substring) or `"Button:Save"` (role-qualified).
/// </summary>
internal static class OneShot
{
    private static readonly string[] Perception = ["apps", "tree", "find", "read", "focused", "snapshot"];
    private static readonly string[] Actions = ["click", "click-at", "invoke", "set-text", "type", "press", "launch"];

    public static bool CanHandle(string? verb) =>
        verb is not null && (Perception.Contains(verb) || Actions.Contains(verb));

    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args[0];
        string? Opt(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        // Positional operands. `launch` forwards everything (minus the gate flag)
        // verbatim so the launched program's own --flags survive; other verbs strip
        // known flags and their values. `--` always means "verbatim from here".
        var flagsWithValue = new HashSet<string> { "--app", "--depth", "--action", "--button", "--scope" };
        var operands = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--") { operands.AddRange(args[(i + 1)..]); break; }
            if (verb == "launch") { if (args[i] != "--enable-actions") operands.Add(args[i]); continue; }
            if (flagsWithValue.Contains(args[i])) { i++; continue; }
            if (args[i].StartsWith("--")) continue;
            operands.Add(args[i]);
        }
        var app = Opt("--app");

        if (Actions.Contains(verb) && !args.Contains("--enable-actions"))
        {
            Console.Error.WriteLine("Refusing to act without --enable-actions (read-only by default).");
            return 2;
        }

        // `launch` must not touch the accessibility bus: over SSH there may be no
        // connectable desktop session for this process at all — that's the point.
        if (verb == "launch")
            return await LaunchAsync(operands);

        await using var provider = new BackendProvider();
        try
        {
            var backend = await provider.GetConnectedAsync();
            return verb switch
            {
                "apps" => Print(await backend.ListApplicationsAsync()),
                "focused" => Print(await backend.GetFocusedAsync()),
                "tree" => await TreeAsync(provider, backend, app, Opt("--depth")),
                "find" => await FindAsync(provider, Operand(operands, 0, "find \"<query>\""), app, Opt("--scope")),
                "read" => Print(await ResolveAsync(provider, Operand(operands, 0, "read \"<query>\""), app)),
                "snapshot" => await SnapshotAsync(provider, backend, app),
                "click" => await ActAsync(provider, Operand(operands, 0, "click \"<query>\""), app,
                    (b, r) => b.ClickAsync(r, ParseButton(Opt("--button")))),
                "invoke" => await ActAsync(provider, Operand(operands, 0, "invoke \"<query>\""), app,
                    (b, r) => b.InvokeAsync(r, Opt("--action"))),
                "set-text" => await ActAsync(provider, Operand(operands, 0, "set-text \"<query>\" \"<text>\""), app,
                    (b, r) => b.SetTextAsync(r, Operand(operands, 1, "set-text \"<query>\" \"<text>\""))),
                "click-at" => await ClickAtAsync(backend, operands),
                "type" => PrintAction("type", "(focused)",
                    await backend.TypeTextAsync(Operand(operands, 0, "type \"<text>\""))),
                "press" => PrintAction("press", Operand(operands, 0, "press \"<keys>\""),
                    await backend.PressKeysAsync(Operand(operands, 0, "press \"<keys>\""))),
                _ => 2,
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"Usage: telekinesis {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, PerceptionTools.Json));
            return 1;
        }
    }

    private sealed class UsageException(string usage) : Exception(usage);

    private static string Operand(List<string> operands, int index, string usage) =>
        index < operands.Count ? operands[index] : throw new UsageException(usage);

    private static PointerButton ParseButton(string? s) =>
        s is null ? PointerButton.Left
        : Enum.TryParse<PointerButton>(s, ignoreCase: true, out var b) ? b
        : throw new UsageException($"--button {s} (expected left, middle or right)");

    private static int Print(object? data)
    {
        Console.WriteLine(data is null ? "null" : JsonSerializer.Serialize(data, PerceptionTools.Json));
        return 0;
    }

    // ---- query addressing ----

    /// <summary>Parse `"Button:Save"` / `"Save"` into role + name-substring criteria.</summary>
    internal static (AccessibleRole? Role, string Name) ParseQuery(string query)
    {
        var colon = query.IndexOf(':');
        if (colon > 0 && Enum.TryParse<AccessibleRole>(query[..colon], ignoreCase: true, out var role))
            return (role, query[(colon + 1)..]);
        return (null, query);
    }

    /// <summary>Best live match for a query, preferring exact name, then visible+enabled.</summary>
    internal static AccessibleElement? PickBest(IReadOnlyList<AccessibleElement> matches, string name) =>
        matches.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? matches.FirstOrDefault(m =>
            (m.States & ElementState.Visible) != 0 && (m.States & ElementState.Enabled) != 0)
        ?? matches.FirstOrDefault();

    private static async Task<AccessibleElement> ResolveAsync(BackendProvider provider, string query, string? app)
    {
        var (role, name) = ParseQuery(query);
        var backend = await provider.GetForAppAsync(app);
        var matches = await backend.FindElementsAsync(new ElementQuery
        {
            ApplicationId = app,
            Role = role,
            NameContains = string.IsNullOrEmpty(name) ? null : name,
            MaxResults = 10,
        });
        return PickBest(matches, name)
            ?? throw new InvalidOperationException($"No element matching \"{query}\"" +
                (app is null ? "" : $" in {app}") + ".");
    }

    private static async Task<int> ActAsync(BackendProvider provider, string query, string? app,
        Func<IAccessibilityBackend, ElementRef, Task<ActionResult>> act)
    {
        var target = await ResolveAsync(provider, query, app);
        // Act through the same app-scoped backend that resolved the target, so
        // provider plugins (browser, Medium) keep owning the action path too.
        var backend = await provider.GetForAppAsync(app);
        var result = await act(backend, target.Ref);
        AuditLog.Append("cli", $"{query} → [{target.Role}] \"{target.Name}\"", result.Success, result.Path.ToString());
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = result.Success,
            path = result.Path.ToString(),
            error = result.Error,
            target = new { id = target.Ref.Id, app = target.Ref.ApplicationId, role = target.Role.ToString(), name = target.Name },
        }, PerceptionTools.Json));
        return result.Success ? 0 : 1;
    }

    private static int PrintAction(string verb, string targetLabel, ActionResult result)
    {
        AuditLog.Append("cli-" + verb, targetLabel, result.Success, result.Path.ToString());
        Console.WriteLine(JsonSerializer.Serialize(
            new { ok = result.Success, path = result.Path.ToString(), error = result.Error }, PerceptionTools.Json));
        return result.Success ? 0 : 1;
    }

    private static async Task<int> ClickAtAsync(IAccessibilityBackend backend, List<string> operands)
    {
        if (operands.Count < 2 || !int.TryParse(operands[0], out var x) || !int.TryParse(operands[1], out var y))
            throw new UsageException("click-at <x> <y>");
        if (backend is not IPointerInjectionBackend pointer)
            throw new NotSupportedException($"{backend.Name} does not support coordinate clicks yet.");
        return PrintAction("click-at", $"({x},{y})", await pointer.ClickAtAsync(x, y));
    }

    // ---- perception composites ----

    private static async Task<int> TreeAsync(
        BackendProvider provider, IAccessibilityBackend backend, string? app, string? depthOpt)
    {
        if (app is null)
        {
            var focused = await backend.GetFocusedAsync()
                ?? throw new InvalidOperationException("No --app given and nothing has focus.");
            app = focused.Ref.ApplicationId;
        }
        var scoped = await provider.GetForAppAsync(app);
        return Print(await scoped.GetTreeAsync(app, int.TryParse(depthOpt, out var d) ? d : 3));
    }

    private static async Task<int> FindAsync(BackendProvider provider, string query, string? app, string? scope)
    {
        var (role, name) = ParseQuery(query);
        var backend = await provider.GetForAppAsync(app);
        var q = new ElementQuery
        {
            ApplicationId = app,
            Role = role,
            NameContains = string.IsNullOrEmpty(name) ? null : name,
        };
        switch (scope)
        {
            case null or "window":
                break;
            case "chrome":
                if (app is null) throw new ArgumentException("--scope chrome requires --app.");
                q = q with { ExcludeDocumentContent = true };
                break;
            case "page":
                if (app is null) throw new ArgumentException("--scope page requires --app.");
                var doc = await BrowserPages.FindDocumentAsync(backend, app, titleContains: null, default)
                    ?? throw new InvalidOperationException(BrowserPages.NoDocumentHint);
                q = q with { Within = doc.Ref };
                break;
            default:
                throw new UsageException($"--scope {scope} (expected window, page or chrome)");
        }
        return Print(await backend.FindElementsAsync(q));
    }

    /// <summary>
    /// One call → the app's current actionable elements with a re-resolvable
    /// `query` per element, so an agent gets a usable map in a single round trip.
    /// </summary>
    private static async Task<int> SnapshotAsync(
        BackendProvider provider, IAccessibilityBackend backend, string? app)
    {
        if (app is null)
        {
            var focused = await backend.GetFocusedAsync()
                ?? throw new InvalidOperationException("No --app given and nothing has focus.");
            app = focused.Ref.ApplicationId;
        }
        var scoped = await provider.GetForAppAsync(app);
        var all = await scoped.FindElementsAsync(new ElementQuery { ApplicationId = app, MaxResults = 400 });
        var actionable = all
            .Where(e => BrowserPages.IsInteractive(e.Role) && !string.IsNullOrWhiteSpace(e.Name)
                        && (e.States & ElementState.Visible) != 0)
            .ToList();
        return Print(new
        {
            app,
            elements = actionable.Select(e => new
            {
                label = e.Name,
                query = $"{e.Role}:{e.Name}",
                role = e.Role.ToString(),
                bounds = e.Bounds,
                enabled = (e.States & ElementState.Enabled) != 0,
            }),
            truncated = all.Count >= 400,
        });
    }

    // ---- launch into the interactive desktop ----

    /// <summary>
    /// Start a GUI program on the *interactive* desktop. Plain Process.Start from an
    /// SSH session on Windows lands in a non-interactive session where nothing renders
    /// and UIA sees nothing — so on Windows this routes through a one-shot Scheduled
    /// Task, which the Task Scheduler runs in the logged-on user's console session.
    /// </summary>
    private static async Task<int> LaunchAsync(List<string> operands)
    {
        if (operands.Count == 0)
        {
            Console.Error.WriteLine("Usage: telekinesis launch <exe> [args…] --enable-actions");
            return 2;
        }
        var exe = operands[0];
        var exeArgs = operands.Skip(1).ToList();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // ponytail: schtasks over WTSQueryUserToken/CreateProcessAsUser — no
                // SYSTEM privilege needed, works from a plain user SSH session for the
                // same logged-on user. Upgrade to the token API if cross-user launch matters.
                var task = $"telekinesis-launch-{Guid.NewGuid():N}";
                // /TR is a string schtasks re-parses; embedded quotes inside tokens are
                // a quoting minefield across cmd/CreateProcess/TaskScheduler, so refuse
                // them outright rather than store a malformed action.
                if (operands.Any(a => a.Contains('"')))
                    throw new InvalidOperationException("launch arguments must not contain '\"' characters.");
                var tr = string.Join(' ',
                    operands.Select(a => a.Any(char.IsWhiteSpace) || a.Length == 0 ? $"\"{a}\"" : a));
                // /IT = run interactively in the logged-on user's session — the point of this path.
                await SchtasksAsync("/Create", "/F", "/TN", task, "/SC", "ONCE", "/ST", "00:00", "/IT", "/TR", tr);
                await SchtasksAsync("/Run", "/TN", task);
                // /Run returns when the instance is queued, not started — deleting the
                // definition immediately can drop it on a slow Task Scheduler engine.
                // ponytail: fixed settle delay; poll the task's Last Run Time if it flakes.
                await Task.Delay(2000);
                await SchtasksAsync("/Delete", "/TN", task, "/F");
                AuditLog.Append("cli-launch", tr, true, "schtasks");
                return Print(new { ok = true, method = "schtasks", exe,
                    hint = "Verify with: telekinesis assert --name <window title>" });
            }
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in exeArgs) psi.ArgumentList.Add(a);
            var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            AuditLog.Append("cli-launch", exe, true, "process-start");
            return Print(new { ok = true, method = "process-start", exe, pid = proc.Id });
        }
        catch (Exception ex)
        {
            AuditLog.Append("cli-launch", exe, false, ex.Message);
            Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, PerceptionTools.Json));
            return 1;
        }
    }

    private static async Task SchtasksAsync(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("schtasks")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var drainOut = p.StandardOutput.ReadToEndAsync(); // drain both pipes or risk a full-buffer deadlock
        var err = await p.StandardError.ReadToEndAsync();
        await drainOut;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"schtasks {args[0]} failed ({p.ExitCode}): {err.Trim()}");
    }
}
