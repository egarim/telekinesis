using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// The provider registry: the OS backend is the base of the resolution ladder;
/// plugins claim specific applications and wrap the backend with higher-fidelity
/// perception/action for them. Built-in providers (browser, vision fallback)
/// ship trusted; external plugin assemblies load ONLY when explicitly listed in
/// plugins.json in the state directory — never by directory scanning — and an
/// untrusted plugin has the same power as the server itself (README §Security).
/// </summary>
public sealed class ProviderRegistry
{
    /// <summary>One loaded provider: the plugin plus where it came from.</summary>
    public sealed record Entry(IProviderPlugin Plugin, bool External, string? Origin);

    public static ProviderRegistry Default { get; } = Load();

    private readonly List<Entry> _entries;
    private readonly ConcurrentDictionary<string, IAccessibilityBackend?> _wrapped = new();
    public IReadOnlyList<Entry> Entries => _entries;
    public IReadOnlyList<string> LoadWarnings { get; }

    private ProviderRegistry(List<Entry> entries, List<string> warnings)
    {
        _entries = [.. entries.OrderByDescending(e => e.Plugin.Priority)];
        LoadWarnings = warnings;
    }

    /// <summary>Tool classes from trusted built-in plugins (always exposed).</summary>
    public IEnumerable<Type> TrustedToolTypes => _entries.Where(e => !e.External).SelectMany(e => e.Plugin.ToolTypes);

    /// <summary>Tool classes from external plugins (exposed only with actions enabled).</summary>
    public IEnumerable<Type> ExternalToolTypes => _entries.Where(e => e.External).SelectMany(e => e.Plugin.ToolTypes);

    /// <summary>
    /// The backend to use for one application: the highest-priority plugin that
    /// claims it (wrapped once, cached), or the base backend when none does.
    /// </summary>
    public IAccessibilityBackend ResolveFor(IAccessibilityBackend baseBackend, string applicationId)
    {
        var wrapped = _wrapped.GetOrAdd(applicationId, _ =>
        {
            var app = DescribeApp(applicationId);
            foreach (var entry in _entries)
            {
                try
                {
                    if (entry.Plugin.Handles(app))
                        return entry.Plugin.Wrap(baseBackend, app);
                }
                catch
                {
                    // A misbehaving plugin must never break base resolution.
                }
            }
            return null;
        });
        return wrapped ?? baseBackend;
    }

    /// <summary>App identity for Handles(): id, process id and process name where knowable.</summary>
    private static ApplicationInfo DescribeApp(string applicationId)
    {
        int? pid = null;
        var name = applicationId;
        if (applicationId.StartsWith("pid:", StringComparison.Ordinal) &&
            int.TryParse(applicationId.AsSpan(4), out var p))
        {
            pid = p;
            try { name = System.Diagnostics.Process.GetProcessById(p).ProcessName; }
            catch { /* gone or inaccessible; keep the id as name */ }
        }
        return new ApplicationInfo(applicationId, name, pid);
    }

    private static ProviderRegistry Load()
    {
        var entries = new List<Entry>
        {
            new(new Providers.BrowserProvider(), External: false, Origin: "built-in"),
            new(new Providers.VisionFallbackProvider(), External: false, Origin: "built-in"),
        };
        var warnings = new List<string>();

        // External plugins: explicit opt-in only, from plugins.json next to the
        // audit log. Format: { "plugins": [ { "path": "abs.dll", "enabled": true } ] }
        var config = Path.Combine(Path.GetDirectoryName(AuditLog.Path)!, "plugins.json");
        if (File.Exists(config))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(config));
                if (doc.RootElement.TryGetProperty("plugins", out var plugins))
                    foreach (var item in plugins.EnumerateArray())
                    {
                        if (item.TryGetProperty("enabled", out var en) && !en.GetBoolean()) continue;
                        var path = item.GetProperty("path").GetString();
                        if (string.IsNullOrEmpty(path)) continue;
                        LoadExternal(path, entries, warnings);
                    }
            }
            catch (Exception ex)
            {
                warnings.Add($"plugins.json unreadable: {ex.Message}");
            }
        }
        return new ProviderRegistry(entries, warnings);
    }

    private static void LoadExternal(string path, List<Entry> entries, List<string> warnings)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var alc = new AssemblyLoadContext($"telekinesis-plugin:{Path.GetFileName(full)}");
            var assembly = alc.LoadFromAssemblyPath(full);
            var found = 0;
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || !typeof(IProviderPlugin).IsAssignableFrom(type)) continue;
                if (Activator.CreateInstance(type) is IProviderPlugin plugin)
                {
                    entries.Add(new Entry(plugin, External: true, Origin: full));
                    found++;
                }
            }
            if (found == 0) warnings.Add($"{full}: no IProviderPlugin types found.");
        }
        catch (Exception ex)
        {
            warnings.Add($"{path}: load failed — {ex.Message}");
        }
    }
}
