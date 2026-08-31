using System.Collections.Concurrent;
using Telekinesis.Abstractions;
using Telekinesis.Linux;
using Telekinesis.Medium;

namespace Telekinesis.Cli;

/// <summary>
/// Selects the platform backend and connects it lazily on first use,
/// so the MCP server starts instantly and permission errors surface
/// as tool errors instead of startup crashes.
/// </summary>
public sealed class BackendProvider : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAccessibilityBackend? _backend;
    private bool _connected;

    /// <summary>Cached Medium manifests per application id (null = app not Medium-enabled).</summary>
    private readonly ConcurrentDictionary<string, MediumManifest?> _medium = new();

    /// <summary>The provider-plugin registry this instance resolves through.</summary>
    public ProviderRegistry Registry { get; } = ProviderRegistry.Default;

    /// <summary>
    /// The backend to use for one application: the highest-priority provider
    /// plugin that claims it (e.g. the browser provider for a browser process),
    /// wrapped by Medium enrichment when the app ships a Medium manifest, or the
    /// base OS backend when neither applies. Null application id → base.
    /// </summary>
    public async Task<IAccessibilityBackend> GetForAppAsync(string? applicationId, CancellationToken ct = default)
    {
        var backend = await GetConnectedAsync(ct);
        if (string.IsNullOrEmpty(applicationId)) return backend;

        // Provider resolution first (browser/vision/etc.), then Medium enrichment on top,
        // so they compose rather than compete. Non-Medium apps are returned untouched.
        var resolved = Registry.ResolveFor(backend, applicationId);
        var medium = _medium.GetOrAdd(applicationId, MediumDiscovery.TryLoad);
        return medium is null ? resolved : new MediumEnrichingBackend(resolved, medium);
    }

    public static IAccessibilityBackend CreateForCurrentOs()
    {
        if (OperatingSystem.IsLinux())
            return new AtSpiBackend();
        if (OperatingSystem.IsWindows())
#if WINDOWS
            return new Telekinesis.Windows.UiaBackend();
#else
            throw new PlatformNotSupportedException(
                "This build does not include the Windows backend; build/run the net10.0-windows target.");
#endif
        if (OperatingSystem.IsMacOS())
            return new Telekinesis.MacOS.AxBackend();
        throw new PlatformNotSupportedException("Unsupported operating system.");
    }

    public async Task<IAccessibilityBackend> GetConnectedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _backend ??= CreateForCurrentOs();
            if (!_connected)
            {
                await _backend.ConnectAsync(ct);
                _connected = true;
            }
            return _backend;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>For doctor: creates without requiring a successful connection.</summary>
    public IAccessibilityBackend GetOrCreateUnconnected()
    {
        _backend ??= CreateForCurrentOs();
        return _backend;
    }

    public async ValueTask DisposeAsync()
    {
        if (_backend is not null) await _backend.DisposeAsync();
        _gate.Dispose();
    }
}
