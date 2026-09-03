using Telekinesis.Medium;
using Xunit;

namespace Telekinesis.Medium.Tests;

public class MediumManifestFileTests
{
    [Fact]
    public void Loads_a_valid_sidecar_manifest()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, MediumSchema.FileName), MediumJson.Serialize(Sample()));
            var manifest = MediumManifestFile.TryLoad(dir);
            Assert.NotNull(manifest);
            Assert.Equal("AcmeERP", manifest.Application);
            Assert.Equal("btnCreate", manifest.Views["V"].Elements.Single().AutomationId);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Returns_null_when_no_manifest_present()
    {
        var dir = TempDir();
        try
        {
            Assert.Null(MediumManifestFile.TryLoad(dir));
            Assert.Null(MediumManifestFile.TryLoad(null));
            Assert.Null(MediumManifestFile.TryLoad(""));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Returns_null_for_malformed_manifest_without_throwing()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, MediumSchema.FileName), "{ not valid json ");
            Assert.Null(MediumManifestFile.TryLoad(dir));   // parse failure must not throw
        }
        finally { Directory.Delete(dir, true); }
    }

    private static MediumManifest Sample() => new()
    {
        Application = "AcmeERP",
        Views = new Dictionary<string, MediumView>
        {
            ["V"] = new() { Elements = [new MediumElement
            {
                SemanticId = "invoice.create", Role = "button", Name = "Create Invoice",
                AutomationId = "btnCreate", // exercises the #40 field's round-trip
            }] },
        },
    };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "medium-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
