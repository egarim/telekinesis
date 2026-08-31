using System.Text.Json;
using System.Text.Json.Serialization;

namespace Telekinesis.Medium;

/// <summary>
/// JSON (de)serialization for the Medium manifest, using a stable, versioned shape:
/// camelCase property names, enum values as strings (lowerCamel), and null optional
/// fields omitted. Deterministic — no LLM, no network.
/// </summary>
public static class MediumJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Serialize a manifest to its canonical JSON form.</summary>
    public static string Serialize(MediumManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);

    /// <summary>Deserialize a manifest from JSON; returns null on malformed input.</summary>
    public static MediumManifest? Deserialize(string json) =>
        JsonSerializer.Deserialize<MediumManifest>(json, Options);
}
