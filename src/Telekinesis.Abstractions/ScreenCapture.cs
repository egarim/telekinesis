namespace Telekinesis.Abstractions;

/// <summary>A captured screen image. Pixels align with the injection coordinate space.</summary>
public sealed record ScreenImage(byte[] PngData, int Width, int Height);

/// <summary>
/// Optional backend capability: pixel capture for the vision fallback tier.
/// Backends that can screenshot implement this alongside <see cref="IAccessibilityBackend"/>;
/// consumers test with a type check and degrade gracefully when it is absent.
/// </summary>
public interface IScreenCaptureBackend
{
    /// <summary>
    /// Capture the given region in screen coordinates, or the entire virtual
    /// desktop when null. Coordinates use the same space as element bounds and
    /// input injection, so vision-derived points can be clicked directly.
    /// </summary>
    Task<ScreenImage> CaptureScreenAsync(Bounds? region = null, CancellationToken ct = default);
}

/// <summary>
/// Optional backend capability: click at raw screen coordinates. Needed by the
/// vision tier, whose elements have pixel bounds but no <see cref="ElementRef"/>.
/// </summary>
public interface IPointerInjectionBackend
{
    Task<ActionResult> ClickAtAsync(int x, int y, PointerButton button = PointerButton.Left, CancellationToken ct = default);
}
