namespace Telekinesis.Medium.Blazor;

/// <summary>
/// Collects a Blazor application's Medium semantics and builds the versioned
/// <see cref="MediumManifest"/> it serves. Registered as a singleton; elements can be
/// added at startup (from a generated registration or a <c>&lt;MediumSemantic/&gt;</c>
/// component) and with <c>Register</c>/<c>RegisterView</c>. Registering is idempotent by
/// semantic id — the last registration for an id wins. Deterministic, no LLM, no network.
/// </summary>
public sealed class MediumManifestBuilder
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MediumElement> _global = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, MediumElement>> _views = new(StringComparer.Ordinal);
    private string _application = string.Empty;

    /// <summary>Application identifier included in the manifest (e.g. "AcmeERP").</summary>
    public string Application
    {
        get => _application;
        set => _application = value ?? string.Empty;
    }

    /// <summary>Register an app-global element. Returns true when newly added, false when it replaced a duplicate.</summary>
    public bool Register(MediumElement element)
    {
        if (string.IsNullOrWhiteSpace(element.SemanticId)) return false;
        lock (_gate) { var isNew = !_global.ContainsKey(element.SemanticId); _global[element.SemanticId] = element; return isNew; }
    }

    /// <summary>Register an element under a named view. Returns true when newly added for that view.</summary>
    public bool RegisterView(string viewName, MediumElement element)
    {
        if (string.IsNullOrWhiteSpace(element.SemanticId)) return false;
        lock (_gate)
        {
            if (!_views.TryGetValue(viewName, out var view)) { view = new(StringComparer.Ordinal); _views[viewName] = view; }
            var isNew = !view.ContainsKey(element.SemanticId);
            view[element.SemanticId] = element;
            return isNew;
        }
    }

    /// <summary>Snapshot the current manifest; a semantic id registered in a view takes precedence over the same id globally.</summary>
    public MediumManifest Build()
    {
        lock (_gate)
        {
            var viewIds = _views.Values.SelectMany(v => v.Keys).ToHashSet(StringComparer.Ordinal);
            var globals = _global.Where(kv => !viewIds.Contains(kv.Key))
                                 .Select(kv => kv.Value)
                                 .ToList();
            var views = _views.ToDictionary(kv => kv.Key, kv => new MediumView { Elements = kv.Value.Values.ToList() }, StringComparer.Ordinal);
            return new MediumManifest
            {
                SchemaVersion = MediumSchema.Version,
                Application = Application,
                Views = views,
                Elements = globals,
            };
        }
    }

    /// <summary>Clear all registered elements (used by tests).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _global.Clear();
            _views.Clear();
            _application = string.Empty;
        }
    }
}
