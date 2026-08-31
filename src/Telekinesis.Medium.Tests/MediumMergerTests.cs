using Telekinesis.Abstractions;
using Telekinesis.Medium;
using Xunit;

namespace Telekinesis.Medium.Tests;

public class MediumMergerTests
{
    private static MediumManifest Manifest() => new()
    {
        Application = "AcmeERP",
        Views = new Dictionary<string, MediumView>
        {
            ["InvoiceEditor"] = new() { Elements = new List<MediumElement>
            {
                new() { SemanticId = "invoice.customer", Role = "textbox", Name = "Customer", Intent = "invoice.customer", Actions = ["set_text"] },
                new() { SemanticId = "invoice.create", Role = "button", Name = "Create Invoice", Intent = "invoice.create", Risk = MediumRisk.Write, Actions = ["invoke"] },
                new() { SemanticId = "invoice.delete", Role = "button", Name = "Delete Invoice", Intent = "invoice.delete", Risk = MediumRisk.Destructive, RequiresConfirmation = true, Actions = ["invoke"] },
                new() { SemanticId = "invoice.unknown", Role = "button", Name = "Do Thing" },  // default risk = Unknown
                new() { SemanticId = "save.button", Role = "button", Name = "Save" },
                new() { SemanticId = "save.link", Role = "link", Name = "Save" },
            } },
        },
    };

    private static AccessibleElement Button(string name) => new()
    {
        Ref = new ElementRef("r", "pid:1"),
        Role = AccessibleRole.Button,
        NativeRole = "Button",
        Name = name,
    };

    private static AccessibleElement Link(string name) => new()
    {
        Ref = new ElementRef("r", "pid:1"),
        Role = AccessibleRole.Link,
        NativeRole = "Link",
        Name = name,
    };

    [Fact]
    public void Enrich_copies_declared_metadata()
    {
        var e = MediumMerger.Enrich(Manifest(), Button("Delete Invoice"));
        Assert.Equal("invoice.delete", e.SemanticId);
        Assert.Equal("invoice.delete", e.Intent);
        Assert.Equal("destructive", e.Risk);
        Assert.True(e.RequiresConfirmation);
        Assert.Equal(["invoke"], e.MediumActions);
    }

    [Fact]
    public void Unmatched_element_is_unchanged()
    {
        var e = MediumMerger.Enrich(Manifest(), Button("Not In Manifest"));
        Assert.Null(e.SemanticId);
        Assert.Null(e.Intent);
        Assert.Null(e.Risk);
        Assert.Null(e.RequiresConfirmation);
        Assert.Null(e.MediumActions);
    }

    [Fact]
    public void Unknown_risk_is_not_synthesized_as_safe()
    {
        // Default risk (Unknown) must not appear as "safe" or "unknown".
        var e = MediumMerger.Enrich(Manifest(), Button("Do Thing"));
        Assert.Null(e.Risk);
        Assert.Null(e.RequiresConfirmation);
    }

    [Fact]
    public void False_confirmation_is_omitted_not_noisy()
    {
        // "Create Invoice" declares Risk.Write, requiresConfirmation defaults false.
        var e = MediumMerger.Enrich(Manifest(), Button("create invoice"));  // name match is case-insensitive
        Assert.Equal("invoice.create", e.SemanticId);
        Assert.Equal("write", e.Risk);
        Assert.Null(e.RequiresConfirmation);
    }

    [Fact]
    public void Case_insensitive_name_match_and_role_disambiguation()
    {
        // Two "Save" elements (button + link); a Button should resolve to the button one.
        var button = MediumMerger.Enrich(Manifest(), Button("Save"));
        Assert.Equal("save.button", button.SemanticId);

        var link = MediumMerger.Enrich(Manifest(), Link("Save"));
        Assert.Equal("save.link", link.SemanticId);
    }

    [Fact]
    public void Unnamed_elements_shadow_everything()
    {
        var e = MediumMerger.Enrich(Manifest(), Button("Create Invoice") with { Name = null, Text = "Delete Invoice" });
        // Name is the matching key; a null name never matches Medium.
        Assert.Null(e.SemanticId);
    }

    [Fact]
    public void Existing_description_is_not_overwritten()
    {
        var element = Button("Delete Invoice") with { Description = "native desc" };
        var e = MediumMerger.Enrich(Manifest(), element);
        Assert.Equal("native desc", e.Description);
    }

    [Fact]
    public void Tree_enrichment_recurses_into_children()
    {
        var child = Button("Create Invoice") with { Ref = new ElementRef("c", "pid:1") };
        var root = new AccessibleElement
        {
            Ref = new ElementRef("root", "pid:1"),
            Role = AccessibleRole.Window,
            NativeRole = "Window",
            Name = "Invoice Editor",
            Children = [child],
        };
        var enriched = MediumMerger.EnrichTree(Manifest(), root);
        Assert.Equal("invoice.create", enriched.Children!.Single().SemanticId);
    }
}
