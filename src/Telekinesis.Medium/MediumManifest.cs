namespace Telekinesis.Medium;

/// <summary>A group of semantic elements that belong together (a screen/view/route).</summary>
public sealed record MediumView
{
    /// <summary>Human label for the view, if known.</summary>
    public string? Label { get; init; }

    /// <summary>The semantic elements on this view.</summary>
    public IReadOnlyList<MediumElement> Elements { get; init; } = [];
}

/// <summary>
/// The versioned, machine-readable Medium manifest (<c>telekinesis.medium.json</c>).
/// This is <em>not</em> an MCP server or an alternate tool protocol — it is semantic
/// metadata Telekinesis merges onto the runtime accessibility tree.
/// </summary>
public sealed record MediumManifest
{
    /// <summary>Manifest schema version (see <see cref="MediumSchema.Version"/>).</summary>
    public string SchemaVersion { get; init; } = MediumSchema.Version;

    /// <summary>Application identifier, e.g. <c>AcmeERP</c>.</summary>
    public string Application { get; init; } = string.Empty;

    /// <summary>Named views in this application, keyed by view name.</summary>
    public IReadOnlyDictionary<string, MediumView> Views { get; init; } = new Dictionary<string, MediumView>();

    /// <summary>App-global elements not owned by a particular view.</summary>
    public IReadOnlyList<MediumElement> Elements { get; init; } = [];
}

/// <summary>Constants for the Medium schema.</summary>
public static class MediumSchema
{
    /// <summary>Current manifest schema version.</summary>
    public const string Version = "1.0";

    /// <summary>Canonical manifest filename.</summary>
    public const string FileName = "telekinesis.medium.json";
}
