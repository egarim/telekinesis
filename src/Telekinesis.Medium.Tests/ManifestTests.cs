using System.Text.Json;
using Telekinesis.Medium;
using Xunit;

namespace Telekinesis.Medium.Tests;

public class ManifestTests
{
    private static MediumManifest SampleManifest() => new()
    {
        SchemaVersion = MediumSchema.Version,
        Application = "AcmeERP",
        Views = new Dictionary<string, MediumView>
        {
            ["InvoiceEditor"] = new()
            {
                Elements =
                [
                    new MediumElement
                    {
                        SemanticId = "invoice.customer",
                        Role = "textbox",
                        Name = "Customer",
                        Actions = ["set_text"],
                    },
                    new MediumElement
                    {
                        SemanticId = "invoice.create",
                        Role = "button",
                        Name = "Create Invoice",
                        Intent = "invoice.create",
                        Risk = MediumRisk.Write,
                        Actions = ["invoke"],
                    },
                ],
            },
        },
    };

    [Fact]
    public void RoundTrips_through_json()
    {
        var manifest = SampleManifest();
        var json = MediumJson.Serialize(manifest);
        var back = MediumJson.Deserialize(json);

        Assert.NotNull(back);
        Assert.Equal(MediumSchema.Version, back.SchemaVersion);
        Assert.Equal("AcmeERP", back.Application);
        var view = back.Views["InvoiceEditor"];
        Assert.Equal(2, view.Elements.Count);

        var create = view.Elements.First(e => e.SemanticId == "invoice.create");
        Assert.Equal("button", create.Role);
        Assert.Equal("Create Invoice", create.Name);
        Assert.Equal(MediumRisk.Write, create.Risk);
        Assert.Equal(["invoke"], create.Actions);
    }

    [Fact]
    public void Serializes_to_the_documented_shape()
    {
        var json = MediumJson.Serialize(SampleManifest());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("AcmeERP", root.GetProperty("application").GetString());

        var view = root.GetProperty("views").GetProperty("InvoiceEditor");
        var elements = view.GetProperty("elements");
        Assert.Equal(2, elements.GetArrayLength());

        var create = elements.EnumerateArray().First(e => e.GetProperty("semanticId").GetString() == "invoice.create");
        // enum values are strings (camelCase), matching the documented manifest.
        Assert.Equal("write", create.GetProperty("risk").GetString());
        Assert.Equal("invoice.create", create.GetProperty("intent").GetString());

        // fields are camelCase.
        Assert.True(create.TryGetProperty("requiresConfirmation", out _));
    }

    [Fact]
    public void Null_optional_fields_are_omitted()
    {
        var manifest = new MediumManifest
        {
            Application = "App",
            Views = new Dictionary<string, MediumView>
            {
                ["V"] = new() { Elements = [new MediumElement { SemanticId = "x", Role = "button" }] },
            },
        };
        var json = MediumJson.Serialize(manifest);
        Assert.DoesNotContain("\"description\"", json);
        Assert.DoesNotContain("\"intent\"", json);
    }

    [Fact]
    public void Unknown_risk_default_is_preserved_not_guessed_safe()
    {
        var json = """
        {
          "schemaVersion": "1.0",
          "application": "App",
          "views": {
            "V": {
              "elements": [
                { "semanticId": "x.delete", "role": "button", "name": "X" }
              ]
            }
          }
        }
        """;
        var manifest = MediumJson.Deserialize(json);
        Assert.NotNull(manifest);
        var element = manifest.Views["V"].Elements.Single();
        Assert.Equal(MediumRisk.Unknown, element.Risk);   // NOT guessed safe
        Assert.False(element.RequiresConfirmation);
    }

    [Fact]
    public void Secrets_are_not_part_of_the_public_model()
    {
        // The model is metadata-only; there is no field that could carry a credential.
        var element = new MediumElement { SemanticId = "x", Role = "button" };
        Assert.Empty(element.Metadata);
        Assert.Empty(element.Actions);
    }

    [Fact]
    public void Deserialize_handles_relationships_and_metadata()
    {
        var json = """
        {
          "schemaVersion": "1.0",
          "application": "App",
          "views": {
            "V": {
              "elements": [
                {
                  "semanticId": "invoice.customer",
                  "role": "textbox",
                  "name": "Customer",
                  "relationships": [ { "type": "labelledby", "target": "label.customer" } ],
                  "metadata": { "maxLength": 50 }
                }
              ]
            }
          }
        }
        """;
        var manifest = MediumJson.Deserialize(json);
        Assert.NotNull(manifest);
        var element = manifest.Views["V"].Elements.Single();
        Assert.Equal("label.customer", element.Relationships.Single().Target);
        Assert.Equal(50, ((JsonElement)element.Metadata["maxLength"]!).GetInt32());
    }
}
