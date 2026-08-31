namespace Telekinesis.Medium;

/// <summary>
/// Loads the Medium manifest (<c>telekinesis.medium.json</c>) that an application ships.
/// Phase 2 uses a <em>sidecar</em> scan: a manifest file next to the application
/// executable. This is local-only and requires no network listener. A malformed or
/// missing manifest is treated as "no Medium" (never throws, never breaks the app).
/// </summary>
public static class MediumManifestFile
{
    /// <summary>Full path to the manifest in a directory, if present.</summary>
    public static string? Find(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var path = Path.Combine(directory, MediumSchema.FileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Load the manifest from a directory, or null when absent/invalid.</summary>
    public static MediumManifest? TryLoad(string? directory)
    {
        var path = Find(directory);
        if (path is null) return null;
        try
        {
            return MediumJson.Deserialize(File.ReadAllText(path));
        }
        catch
        {
            // A manifest we can't parse must not break the app — treat as no Medium.
            return null;
        }
    }
}
