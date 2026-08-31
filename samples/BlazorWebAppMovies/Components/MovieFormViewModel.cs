using Telekinesis.Medium;

namespace BlazorWebAppMovies.Components;

/// <summary>
/// The movie form's command surface, annotated for Medium. The Roslyn generator scans
/// these <c>[Medium*]</c> attributes at build time and emits semantics (stable ids,
/// intent, risk, confirmation) that enrich the same controls Telekinesis perceives.
/// </summary>
public sealed class MovieFormViewModel
{
    [MediumIntent("movie.create")]
    [MediumRisk(MediumRisk.Write)]
    public object CreateMovieCommand { get; set; } = new();

    [MediumIntent("movie.update")]
    [MediumRisk(MediumRisk.Write)]
    public object UpdateMovieCommand { get; set; } = new();

    [MediumSemanticId("movie.delete")]
    [MediumIntent("movie.delete")]
    [MediumRisk(MediumRisk.Destructive)]
    [MediumRequiresConfirmation]
    public object DeleteMovieCommand { get; set; } = new();
}
