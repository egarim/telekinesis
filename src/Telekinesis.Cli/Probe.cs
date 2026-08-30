using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// `telekinesis probe` — exercises the backend directly from the terminal, no MCP
/// client required. Used for VM validation and as a filmable smoke test:
///   telekinesis probe                     list applications
///   telekinesis probe --app <id> --depth 2   walk one app's tree
///   telekinesis probe --find "Save"       find elements by name substring
///   telekinesis probe --click "Save"      find a button by name and click it (action test)
///   telekinesis probe --type "hello"      type into the focused element
///   telekinesis probe --keys "ctrl+s"     press a key combination
/// Action flags are gated behind --enable-actions so a bare probe is read-only.
/// </summary>
internal static class Probe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Opt(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var app = Opt("--app");
        var find = Opt("--find");
        var click = Opt("--click");
        var type = Opt("--type");
        var keys = Opt("--keys");
        var setText = Opt("--set-text");
        var setValue = Opt("--set-value");
        var screenshot = Opt("--screenshot");
        var overlay = args.Contains("--overlay");
        var parse = args.Contains("--parse");
        var clickAt = Opt("--click-at");
        var region = Opt("--region");
        var depth = int.TryParse(Opt("--depth"), out var d) ? d : 2;
        var actionsEnabled = args.Contains("--enable-actions");

        await using var provider = new BackendProvider();
        IAccessibilityBackend backend;
        try
        {
            backend = await provider.GetConnectedAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connect failed: {ex.Message}");
            Console.Error.WriteLine("Run `telekinesis doctor` to diagnose.");
            return 1;
        }

        Console.WriteLine($"Connected via {backend.Name}.\n");

        try
        {
            if (overlay)
                return await RunOverlayAsync(backend, app, find,
                    int.TryParse(Opt("--for"), out var forSecs) ? forSecs : null);
            if (args.Contains("--recall"))
                return await RunRecallAsync(provider, app, args.Contains("--show"));
            if (screenshot is not null)
                return await RunScreenshotAsync(backend, screenshot, region);
            if (parse)
                return await RunParseAsync(backend, region);
            if (clickAt is not null)
            {
                if (!actionsEnabled)
                {
                    Console.Error.WriteLine("Refusing to act without --enable-actions (read-only by default).");
                    return 2;
                }
                return await RunClickAtAsync(backend, clickAt);
            }
            if (setText is not null)
            {
                if (!actionsEnabled)
                {
                    Console.Error.WriteLine("Refusing to act without --enable-actions (read-only by default).");
                    return 2;
                }
                return await RunSetTextAsync(backend, app, find, setText);
            }
            if (setValue is not null)
            {
                if (!actionsEnabled)
                {
                    Console.Error.WriteLine("Refusing to act without --enable-actions (read-only by default).");
                    return 2;
                }
                return await RunSetValueAsync(backend, app, find, setValue);
            }
            return await DispatchAsync(backend, app, find, click, type, keys, depth, actionsEnabled);
        }
        catch (Exception ex)
        {
            // Full diagnostics: on the first VM run, a marshalling bug should report
            // the exact failing call and stack, not just a one-line message.
            Console.Error.WriteLine("\n--- probe failed ---");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(IAccessibilityBackend backend, string? app, string? find,
        string? click, string? type, string? keys, int depth, bool actionsEnabled)
    {
        // Action tests first (they need a target); otherwise fall through to inspection.
        if (click is not null || type is not null || keys is not null)
        {
            if (!actionsEnabled)
            {
                Console.Error.WriteLine("Refusing to act without --enable-actions (read-only by default).");
                return 2;
            }
            return await RunActionsAsync(backend, app, click, type, keys);
        }

        if (find is not null)
        {
            var results = await backend.FindElementsAsync(new ElementQuery
            {
                ApplicationId = app,
                NameContains = find,
                MaxResults = 25,
            });
            Console.WriteLine($"Found {results.Count} element(s) matching \"{find}\":");
            foreach (var e in results)
                Console.WriteLine($"  [{e.Role}] {Quote(e.Name)}  {Fmt(e.Bounds)}  states={e.States}  id={e.Ref.Id}");
            return 0;
        }

        if (app is not null)
        {
            Console.WriteLine($"Tree of {app} (depth {depth}):");
            var tree = await backend.GetTreeAsync(app, depth);
            PrintTree(tree, 0);
            return 0;
        }

        var apps = await backend.ListApplicationsAsync();
        Console.WriteLine($"{apps.Count} application(s) on the accessibility bus:");
        foreach (var a in apps)
            Console.WriteLine($"  {a.Name}   (id: {a.Id})");
        Console.WriteLine("\nInspect one with:  telekinesis probe --app <id> --depth 2");
        return 0;
    }

    /// <summary>Full demo flow: find an editable element, set its text natively, read it back to verify.</summary>
    private static async Task<int> RunSetTextAsync(IAccessibilityBackend backend, string? app, string? find, string text)
    {
        // Prefer an explicitly named target; otherwise take the first editable element.
        // Editors surface as Edit or Document depending on the toolkit (Windows 11 Notepad
        // is a Document), so try each role until something matches.
        IReadOnlyList<AccessibleElement> matches = [];
        foreach (var role in string.IsNullOrEmpty(find)
                     ? new AccessibleRole?[] { AccessibleRole.Edit, AccessibleRole.Document, AccessibleRole.Text }
                     : new AccessibleRole?[] { null })
        {
            matches = await backend.FindElementsAsync(new ElementQuery
            {
                ApplicationId = app,
                NameContains = string.IsNullOrEmpty(find) ? null : find,
                Role = role,
                MaxResults = 5,
            });
            if (matches.Count > 0) break;
        }
        var editable = matches.Where(m =>
            m.Role is AccessibleRole.Text or AccessibleRole.Edit or AccessibleRole.Document).ToList();
        // When no explicit target was named, prefer an empty field so we never clobber a
        // document that already has content.
        var target = (string.IsNullOrEmpty(find)
                ? editable.FirstOrDefault(m => string.IsNullOrEmpty(m.Text))
                : null)
            ?? editable.FirstOrDefault() ?? matches.FirstOrDefault();
        if (target is null)
        {
            Console.Error.WriteLine("No editable element found to fill.");
            return 1;
        }
        Console.WriteLine($"Target: [{target.Role}] {Quote(target.Name)} {Fmt(target.Bounds)}");
        Console.WriteLine($"Setting text: \"{text}\" ...");
        var r = await backend.SetTextAsync(target.Ref, text);
        Console.WriteLine($"  → success={r.Success} path={r.Path} {r.Error}");
        if (!r.Success) return 1;
        await new VisionMemoryService().LearnFromElementAsync(backend, target.Ref);

        // Read it back through AT-SPI to prove the text really landed in the app.
        var after = await backend.ReadElementAsync(target.Ref);
        Console.WriteLine($"Read-back text: {Quote(after.Text)}");
        var ok = after.Text is not null && after.Text.Contains(text.Split('\n')[0]);
        Console.WriteLine(ok ? "VERIFIED: the app now contains the text." : "WARNING: read-back did not match.");
        return ok ? 0 : 1;
    }

    /// <summary>Set a numeric value (slider, spinner) natively via the RangeValue pattern, then read it back.</summary>
    private static async Task<int> RunSetValueAsync(IAccessibilityBackend backend, string? app, string? find, string valueText)
    {
        if (string.IsNullOrEmpty(find))
        {
            Console.Error.WriteLine("--set-value needs --find <name> to pick the target element.");
            return 2;
        }
        if (!double.TryParse(valueText, out var value))
        {
            Console.Error.WriteLine($"--set-value: \"{valueText}\" is not a number.");
            return 2;
        }
        var matches = await backend.FindElementsAsync(new ElementQuery
        {
            ApplicationId = app, NameContains = find, Role = AccessibleRole.Slider, MaxResults = 5,
        });
        if (matches.Count == 0)
            matches = await backend.FindElementsAsync(new ElementQuery
            {
                ApplicationId = app, NameContains = find, MaxResults = 5,
            });
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"No element matching \"{find}\".");
            return 1;
        }
        var target = matches[0];
        Console.WriteLine($"Target: [{target.Role}] {Quote(target.Name)} {Fmt(target.Bounds)}");
        Console.WriteLine($"Setting value: {value} ...");
        var r = await backend.SetValueAsync(target.Ref, value);
        Console.WriteLine($"  → success={r.Success} path={r.Path} {r.Error}");
        if (!r.Success) return 1;

        var after = await backend.ReadElementAsync(target.Ref);
        Console.WriteLine($"Read-back value: {after.Value?.ToString() ?? "(none)"}");
        var ok = after.Value is not null && Math.Abs(after.Value.Value - value) < 0.5;
        Console.WriteLine(ok ? "VERIFIED: the control now holds the value." : "WARNING: read-back did not match.");
        return ok ? 0 : 1;
    }

    private static async Task<int> RunActionsAsync(IAccessibilityBackend backend, string? app, string? click, string? type, string? keys)
    {
        if (click is not null)
        {
            var matches = await backend.FindElementsAsync(new ElementQuery
            {
                ApplicationId = app, Role = AccessibleRole.Button, NameContains = click, MaxResults = 1,
            });
            // Not everything clickable is a Button (check boxes, menu items, list items…) —
            // fall back to a name-only match and let the backend pick the right pattern.
            if (matches.Count == 0)
                matches = await backend.FindElementsAsync(new ElementQuery
                {
                    ApplicationId = app, NameContains = click, MaxResults = 1,
                });
            if (matches.Count == 0)
            {
                Console.Error.WriteLine($"Nothing matching \"{click}\" to click.");
                return 1;
            }
            var target = matches[0];
            Console.WriteLine($"Invoking [{target.Role}] {Quote(target.Name)} {Fmt(target.Bounds)} ...");
            var r = await backend.InvokeAsync(target.Ref);
            Console.WriteLine($"  → success={r.Success} path={r.Path} {r.Error}");
            if (r.Success)
                await new VisionMemoryService().LearnFromElementAsync(backend, target.Ref);
            return r.Success ? 0 : 1;
        }
        if (type is not null)
        {
            Console.WriteLine($"Typing into focused element: \"{type}\" ...");
            var r = await backend.TypeTextAsync(type);
            Console.WriteLine($"  → success={r.Success} path={r.Path} {r.Error}");
            return r.Success ? 0 : 1;
        }
        // keys
        Console.WriteLine($"Pressing keys: {keys} ...");
        var rk = await backend.PressKeysAsync(keys!);
        Console.WriteLine($"  → success={rk.Success} path={rk.Path} {rk.Error}");
        return rk.Success ? 0 : 1;
    }

    // ---- X-ray overlay ----

    /// <summary>
    /// `probe --overlay --app pid:N [--find substr]` — draw live labeled boxes over an
    /// app's elements on the real desktop, refreshed until Enter. The "what the AI sees" shot.
    /// </summary>
    private static async Task<int> RunOverlayAsync(IAccessibilityBackend backend, string? app, string? find, int? forSeconds)
    {
        if (backend is not IVisualFeedbackBackend visual)
        {
            Console.Error.WriteLine($"{backend.Name} does not support the X-ray overlay yet.");
            return 1;
        }
        if (app is null)
        {
            Console.Error.WriteLine("--overlay needs --app <id> (run a bare probe to list applications).");
            return 2;
        }

        // Interactive: refresh until Enter. Unattended (--for N, or no console at
        // all): refresh for N seconds (default 30) — also handy for timed filming.
        Task? stop = null;
        if (forSeconds is null && !Console.IsInputRedirected)
        {
            Console.WriteLine("X-ray overlay on — press Enter to stop.");
            stop = Task.Run(Console.In.ReadLine);
        }
        else
        {
            forSeconds ??= 30;
            Console.WriteLine($"X-ray overlay on for {forSeconds}s.");
        }
        var deadline = DateTimeOffset.Now.AddSeconds(forSeconds ?? int.MaxValue);
        var first = true;
        while (stop is not null ? !stop.IsCompleted : DateTimeOffset.Now < deadline)
        {
            var elements = await backend.FindElementsAsync(new ElementQuery
            {
                ApplicationId = app,
                NameContains = string.IsNullOrEmpty(find) ? null : find,
                MaxResults = 60,
            });
            var targets = elements
                .Where(e => e.Bounds is not null
                            && (e.States & ElementState.Visible) != 0
                            && e.Role is not (AccessibleRole.Window or AccessibleRole.Pane or AccessibleRole.Group))
                .Take(40)
                .ToList();

            var regions = new List<HighlightRegion>(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                var name = targets[i].Name ?? targets[i].Role.ToString();
                regions.Add(new HighlightRegion(targets[i].Bounds!,
                    $"{i + 1} {(name.Length > 20 ? name[..20] + "…" : name)}"));
            }
            // Persist until replaced by the next refresh (or cleared on exit).
            await visual.HighlightAsync(regions, TimeSpan.Zero);

            if (first)
            {
                first = false;
                Console.WriteLine($"{targets.Count} element(s) overlaid:");
                for (var i = 0; i < targets.Count; i++)
                    Console.WriteLine($"  {i + 1,3}  [{targets[i].Role}] {Quote(targets[i].Name)}  {Fmt(targets[i].Bounds)}");
            }
            await Task.Delay(1000);
        }
        await visual.ClearHighlightsAsync();
        Console.WriteLine("Overlay cleared.");
        return 0;
    }

    /// <summary>
    /// `probe --recall --app pid:N [--show]` — re-locate the app's remembered
    /// perceptual anchors on the live screen; --show draws them as X-ray boxes.
    /// </summary>
    private static async Task<int> RunRecallAsync(BackendProvider provider, string? app, bool show)
    {
        if (app is null)
        {
            Console.Error.WriteLine("--recall needs --app <id> (run a bare probe to list applications).");
            return 2;
        }
        var memoryService = new VisionMemoryService();
        var json = await VisionTools.RecallTargets(provider, memoryService, app, show, default);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var targets = doc.RootElement.GetProperty("targets");
        Console.WriteLine($"{targets.GetArrayLength()} remembered target(s) for {doc.RootElement.GetProperty("app")}:");
        foreach (var t in targets.EnumerateArray())
        {
            var b = t.GetProperty("Bounds");
            Console.WriteLine($"  [{t.GetProperty("Type")}] {Quote(t.GetProperty("Caption").GetString())}"
                + $"  @{b.GetProperty("X")},{b.GetProperty("Y")} {b.GetProperty("Width")}x{b.GetProperty("Height")}"
                + $"  score={t.GetProperty("Score")}");
        }
        if (show && targets.GetArrayLength() > 0)
        {
            Console.WriteLine("(boxes on screen for 5 s...)");
            await Task.Delay(5500); // the overlay lives in this process; stay alive while it shows
        }
        return 0;
    }

    // ---- Vision tier ----

    private static async Task<int> RunScreenshotAsync(IAccessibilityBackend backend, string file, string? region)
    {
        if (backend is not IScreenCaptureBackend capture)
        {
            Console.Error.WriteLine($"{backend.Name} does not support screen capture yet.");
            return 1;
        }
        var image = await capture.CaptureScreenAsync(PerceptionTools.ParseRegion(region));
        await File.WriteAllBytesAsync(file, image.PngData);
        Console.WriteLine($"Captured {image.Width}x{image.Height} → {file} ({image.PngData.Length / 1024} KiB)");
        return 0;
    }

    private static async Task<int> RunParseAsync(IAccessibilityBackend backend, string? region)
    {
        if (backend is not IScreenCaptureBackend capture)
        {
            Console.Error.WriteLine($"{backend.Name} does not support screen capture yet.");
            return 1;
        }
        using var parser = new Telekinesis.Vision.OmniParserClient();
        if (!await parser.ProbeAsync())
        {
            Console.Error.WriteLine($"No OmniParser server at {parser.BaseUrl} (see docs/VISION.md, "
                + $"or set {Telekinesis.Vision.OmniParserClient.UrlEnvVar}).");
            return 1;
        }
        var r = PerceptionTools.ParseRegion(region);
        var image = await capture.CaptureScreenAsync(r);
        Console.WriteLine($"Parsing {image.Width}x{image.Height} via {parser.BaseUrl} ...");
        var elements = await parser.ParseAsync(image, r is null ? null : (r.X, r.Y));
        Console.WriteLine($"{elements.Count} element(s):");
        foreach (var e in elements)
            Console.WriteLine($"  [{e.Type}]{(e.Interactive ? "*" : " ")} {Quote(e.Content)}  @{e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height}");
        Console.WriteLine("(* = interactive; click with:  telekinesis probe --enable-actions --click-at \"x,y\")");
        return 0;
    }

    private static async Task<int> RunClickAtAsync(IAccessibilityBackend backend, string point)
    {
        if (backend is not IPointerInjectionBackend pointer)
        {
            Console.Error.WriteLine($"{backend.Name} does not support coordinate clicks yet.");
            return 1;
        }
        var parts = point.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
        {
            Console.Error.WriteLine($"Malformed point '{point}' (expected 'x,y').");
            return 1;
        }
        Console.WriteLine($"Clicking at ({x},{y}) ...");
        var r = await pointer.ClickAtAsync(x, y);
        Console.WriteLine($"  → success={r.Success} path={r.Path} {r.Error}");
        return r.Success ? 0 : 1;
    }

    private static void PrintTree(AccessibleElement e, int indent)
    {
        Console.WriteLine($"{new string(' ', indent * 2)}[{e.Role}] {Quote(e.Name)}  {Fmt(e.Bounds)}"
            + (e.Text is not null ? $"  text={Quote(e.Text)}" : ""));
        if (e.Children is null) return;
        foreach (var c in e.Children) PrintTree(c, indent + 1);
    }

    private static string Quote(string? s) => s is null ? "(unnamed)" : $"\"{s}\"";
    private static string Fmt(Bounds? b) => b is null ? "" : $"@{b.X},{b.Y} {b.Width}x{b.Height}";
}
