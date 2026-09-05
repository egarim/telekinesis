using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    /// <c>/telekinesis.medium.json</c>) — a DEBUG/TOOLING aid, not the runtime
    /// contract (issue #36). Agents read Medium from the accessibility tree
    /// (<see cref="MediumDomMapper"/> attributes merged by the enriching backend);
    /// the manifest is only a serialized rendering of the same metadata for humans
    /// and tooling. By default the endpoint is therefore mapped ONLY in the
    /// Development environment; pass <paramref name="alsoInProduction"/> = true to
    /// deliberately expose it elsewhere (it is advisory-only and grants no powers,
    /// but it is public surface your app does not need).
    /// </summary>
    public static IEndpointRouteBuilder MapMediumManifest(
        this IEndpointRouteBuilder endpoints,
        string path = "/telekinesis.medium.json",
        bool alsoInProduction = false)
    {
        // Fail closed: if the environment can't be resolved, treat it as NOT
        // Development so a missing IHostEnvironment never silently exposes the endpoint.
        var env = endpoints.ServiceProvider.GetService<IHostEnvironment>();
        if (!alsoInProduction && env?.IsDevelopment() != true)
            return endpoints;

        endpoints.MapGet(path, (MediumManifestBuilder builder) =>
            Results.Json(builder.Build(), MediumJson.Options));
        return endpoints;
    }
}
