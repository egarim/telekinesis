using System.Diagnostics;
using Telekinesis.Medium;

namespace Telekinesis.Cli;

/// <summary>
/// Discovers a running application's Medium manifest (issue #28, Phase 2): locate the
/// sidecar <c>telekinesis.medium.json</c> next to the app's executable (process id →
/// main module path → sibling manifest). Returns null when the app is not Medium-enabled
/// or the process is gone/inaccessible — resolution must never break on discovery.
/// </summary>
public static class MediumDiscovery
{
    public static MediumManifest? TryLoad(string applicationId)
    {
        if (!applicationId.StartsWith("pid:", StringComparison.Ordinal) ||
            !int.TryParse(applicationId.AsSpan(4), out var pid))
            return null;

        try
        {
            var exe = Process.GetProcessById(pid).MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return null;
            return MediumManifestFile.TryLoad(Path.GetDirectoryName(exe));
        }
        catch
        {
            // Process gone or inaccessible — treat as not Medium-enabled.
            return null;
        }
    }
}
