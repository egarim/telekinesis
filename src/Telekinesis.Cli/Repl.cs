using System.Diagnostics;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// Persistent interactive session: connect once, then execute commands from stdin.
/// This is the honest way to measure action latency — one-shot `probe` pays process
/// start + JIT + UIA connect on every call, which drowns the tens-of-ms action itself.
/// Every command prints its own elapsed time.
/// </summary>
public static class Repl
{
    public static async Task<int> RunAsync(string[] args)
    {
        var actionsEnabled = args.Contains("--enable-actions");
        using var consoles = new ConsoleSessionService();

        await using var provider = new BackendProvider();
        IAccessibilityBackend backend;
        try
        {
            backend = await provider.GetConnectedAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connect failed: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"Connected via {backend.Name}. Commands: apps | tree <app> [depth] | " +
                          "find <app> <name> | click <app> <name> | settext <app> <name> <text> | " +
                          "setvalue <app> <name> <num> | con-open [shell] | con-write <id> <text> | " +
                          "con-read <id> | con-close <id> | quit");
        if (!actionsEnabled) Console.WriteLine("(read-only: start with --enable-actions to allow click/settext/setvalue)");

        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            var parts = Tokenize(line);
            if (parts.Length == 0) continue;
            var cmd = parts[0].ToLowerInvariant();
            if (cmd is "quit" or "exit") break;

            var sw = Stopwatch.StartNew();
            try
            {
                switch (cmd)
                {
                    case "apps":
                    {
                        var apps = await backend.ListApplicationsAsync();
                        foreach (var a in apps) Console.WriteLine($"  {a.Name}   (id: {a.Id})");
                        break;
                    }
                    case "tree" when parts.Length >= 2:
                    {
                        var depth = parts.Length >= 3 && int.TryParse(parts[2], out var d) ? d : 3;
                        PrintTree(await backend.GetTreeAsync(parts[1], depth), 0);
                        break;
                    }
                    case "find" when parts.Length >= 3:
                    {
                        var found = await backend.FindElementsAsync(new ElementQuery
                        {
                            ApplicationId = parts[1], NameContains = string.Join(' ', parts[2..]), MaxResults = 10,
                        });
                        foreach (var e in found)
                            Console.WriteLine($"  [{e.Role}] \"{e.Name}\" {(e.Text is null ? "" : $"text=\"{e.Text}\" ")}states={e.States}");
                        if (found.Count == 0) Console.WriteLine("  (no match)");
                        break;
                    }
                    case "click" when parts.Length >= 3:
                    {
                        if (!RequireActions(actionsEnabled)) break;
                        // Prefer things made to be clicked — plain Text matching the words
                        // (a label, an error message) must not shadow the actual control.
                        var clickName = string.Join(' ', parts[2..]);
                        var target = await FindOneAsync(backend, parts[1], clickName, AccessibleRole.Button)
                                     ?? await FindOneAsync(backend, parts[1], clickName, AccessibleRole.CheckBox)
                                     ?? await FindOneAsync(backend, parts[1], clickName, AccessibleRole.ListItem)
                                     ?? await FindOneAsync(backend, parts[1], clickName, AccessibleRole.MenuItem)
                                     ?? await FindOneAsync(backend, parts[1], clickName);
                        if (target is null) { Console.WriteLine("  (no match)"); break; }
                        var r = await backend.InvokeAsync(target.Ref);
                        Console.WriteLine($"  [{target.Role}] \"{target.Name}\" → success={r.Success} path={r.Path} {r.Error}");
                        break;
                    }
                    case "expand" or "collapse" or "toggle" when parts.Length >= 3:
                    {
                        if (!RequireActions(actionsEnabled)) break;
                        var target = await FindOneAsync(backend, parts[1], string.Join(' ', parts[2..]));
                        if (target is null) { Console.WriteLine("  (no match)"); break; }
                        var r = await backend.InvokeAsync(target.Ref, cmd);
                        Console.WriteLine($"  [{target.Role}] \"{target.Name}\" → success={r.Success} path={r.Path} {r.Error}");
                        break;
                    }
                    case "settext" when parts.Length >= 4:
                    {
                        if (!RequireActions(actionsEnabled)) break;
                        // Prefer an actual editor: a field's label often carries the same name.
                        var target = await FindOneAsync(backend, parts[1], parts[2], AccessibleRole.Edit)
                                     ?? await FindOneAsync(backend, parts[1], parts[2], AccessibleRole.Document)
                                     ?? await FindOneAsync(backend, parts[1], parts[2]);
                        if (target is null) { Console.WriteLine("  (no match)"); break; }
                        var r = await backend.SetTextAsync(target.Ref, string.Join(' ', parts[3..]));
                        Console.WriteLine($"  [{target.Role}] \"{target.Name}\" → success={r.Success} path={r.Path} {r.Error}");
                        break;
                    }
                    case "setvalue" when parts.Length >= 4 && double.TryParse(parts[^1], out var value):
                    {
                        if (!RequireActions(actionsEnabled)) break;
                        var target = await FindOneAsync(backend, parts[1], string.Join(' ', parts[2..^1]));
                        if (target is null) { Console.WriteLine("  (no match)"); break; }
                        var r = await backend.SetValueAsync(target.Ref, value);
                        Console.WriteLine($"  [{target.Role}] \"{target.Name}\" → success={r.Success} path={r.Path} {r.Error}");
                        break;
                    }
                    case "con-open":
                    {
                        if (!RequireActions(actionsEnabled)) break;
                        var entry = consoles.Open(parts.Length >= 2 ? string.Join(' ', parts[1..]) : null, 120, 30);
                        await Task.Delay(400);
                        Console.WriteLine($"  {entry.Id} ({entry.Session.Shell})");
                        Console.WriteLine(Indent(entry.Screen.Render()));
                        break;
                    }
                    case "con-write" when parts.Length >= 3:
                    {
                        if (!RequireActions(actionsEnabled)) break;
                        var entry = consoles.Get(parts[1]);
                        entry.Session.Write(string.Join(' ', parts[2..]) + "\r");
                        await Task.Delay(400);
                        Console.WriteLine(Indent(entry.Screen.Render(12)));
                        break;
                    }
                    case "con-read" when parts.Length >= 2:
                        Console.WriteLine(Indent(consoles.Get(parts[1]).Screen.Render()));
                        break;
                    case "con-close" when parts.Length >= 2:
                        consoles.Close(parts[1]);
                        Console.WriteLine("  closed");
                        break;
                    default:
                        Console.WriteLine("  ? unknown or incomplete command");
                        break;
                }
            }
            catch (StaleElementException e) { Console.WriteLine($"  stale: {e.Message}"); }
            catch (Exception e) { Console.WriteLine($"  error: {e.Message}"); }
            Console.WriteLine($"  [{sw.ElapsedMilliseconds} ms]");
        }
        return 0;
    }

    private static string Indent(string screen) =>
        string.Join('\n', screen.Split('\n').Select(l => "  | " + l));

    /// <summary>Split on spaces, honoring double quotes: settext pid:1 "Incident date" 2026-08-20</summary>
    private static string[] Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens.ToArray();
    }

    private static bool RequireActions(bool enabled)
    {
        if (!enabled) Console.WriteLine("  refused: start repl with --enable-actions");
        return enabled;
    }

    private static async Task<AccessibleElement?> FindOneAsync(IAccessibilityBackend backend, string app, string name, AccessibleRole? role = null)
    {
        var found = await backend.FindElementsAsync(new ElementQuery
        {
            ApplicationId = app, NameContains = name, Role = role, MaxResults = 1,
        });
        return found.Count > 0 ? found[0] : null;
    }

    private static void PrintTree(AccessibleElement el, int indent)
    {
        var text = el.Text is null ? "" : $"  text=\"{el.Text}\"";
        Console.WriteLine($"{new string(' ', indent * 2)}[{el.Role}] \"{el.Name}\"{text}");
        if (el.Children is not null)
            foreach (var c in el.Children) PrintTree(c, indent + 1);
    }
}
