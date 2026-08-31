using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Telekinesis.Medium.Blazor;

/// <summary>
/// ASP.NET Core wiring for a Medium-enabled Blazor app.
/// </summary>
public static class MediumServices
{
    /// <summary>Register the <see cref="MediumManifestBuilder"/> singleton.</summary>
    public static IServiceCollection AddTelekinesisMedium(this IServiceCollection services) =>
        services.AddSingleton<MediumManifestBuilder>();

    /// <summary>
    /// Serve the Medium manifest at <paramref name="path"/> (default
    /// <c>/telekinesis.medium.json</c>). Call this on the endpoint route builder.
    /// The manifest is read-only/advisory — exposing it grants no powers to a client
    /// that Telekinesis itself does not already have.
    /// </summary>
    public static IEndpointRouteBuilder MapMediumManifest(this IEndpointRouteBuilder endpoints, string path = "/telekinesis.medium.json")
    {
        endpoints.MapGet(path, (MediumManifestBuilder builder) =>
            Results.Json(builder.Build(), MediumJson.Options));
        return endpoints;
    }
}
