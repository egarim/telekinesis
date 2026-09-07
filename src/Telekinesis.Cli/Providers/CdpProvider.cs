using Telekinesis.Abstractions;

namespace Telekinesis.Cli.Providers;

/// <summary>
/// The Chrome DevTools Protocol tier as a built-in plugin (issue #51). Like the
/// vision fallback it claims NO application — the accessibility tree stays the
/// one perception/action path for browsers, and the resolution ladder is
/// untouched. This plugin only contributes tools, and only when the tier is
/// enabled (TELEKINESIS_CDP=1), so the default posture really is "no CDP".
/// </summary>
internal sealed class CdpProvider : IProviderPlugin
{
    public string Name => "cdp";
    public int Priority => int.MinValue;
    public bool Handles(ApplicationInfo app) => false;
    public IAccessibilityBackend Wrap(IAccessibilityBackend baseBackend, ApplicationInfo app) => baseBackend;

    /// <summary>READ tools only. ProviderRegistry splices TrustedToolTypes into the
    /// perception set unconditionally — it loads under --read-only — so nothing
    /// that acts on the page may be returned here.</summary>
    public IEnumerable<Type> ToolTypes =>
        CdpSessionService.Enabled ? [typeof(BrowserTools)] : [];

    /// <summary>Action-tier tools, registered next to ActionTools in Program.cs so
    /// they inherit the --enable-actions / --read-only gate.</summary>
    internal static IEnumerable<Type> ActionToolTypes =>
        CdpSessionService.Enabled ? [typeof(BrowserEvalTools)] : [];
}
