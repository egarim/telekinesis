using Telekinesis.Abstractions;
using Telekinesis.Linux;

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
            throw new PlatformNotSupportedException(
                "The macOS (AXAPI) backend is not implemented yet.");
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
