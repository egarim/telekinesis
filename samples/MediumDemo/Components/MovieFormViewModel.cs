using Telekinesis.Medium;

namespace MediumDemo.Components;

/// <summary>
/// A movie form command surface, modeled on Microsoft's
/// <c>dotnet/blazor-samples</c> <c>BlazorWebAppMovies</c>. The Medium Roslyn generator
/// scans these <c>[Medium*]</c> annotations at build time and turns them into semantic
/// metadata (stable ids, intent, risk, confirmation) that enriches what Telekinesis
/// already perceives through the accessibility tree.
/// </summary>
public sealed class MovieFormViewModel
{
    public string Title { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public decimal Price { get; set; }

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
