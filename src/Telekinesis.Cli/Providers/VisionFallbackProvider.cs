using Telekinesis.Abstractions;

namespace Telekinesis.Cli.Providers;

/// <summary>
/// The vision tier expressed as the built-in fallback plugin: it claims no
/// application (pixels are a last resort the agent reaches for explicitly, not
/// a transparent upgrade) and contributes the vision tools — screenshot,
/// parse_screen, recall_targets — to the perception set, exactly as before the
/// registry existed.
/// </summary>
internal sealed class VisionFallbackProvider : IProviderPlugin
{
    public string Name => "vision-fallback";
    public int Priority => int.MinValue;
    public bool Handles(ApplicationInfo app) => false;
    public IAccessibilityBackend Wrap(IAccessibilityBackend baseBackend, ApplicationInfo app) => baseBackend;
    public IEnumerable<Type> ToolTypes => [typeof(VisionTools)];
}
