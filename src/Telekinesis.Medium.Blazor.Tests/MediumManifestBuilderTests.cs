using Telekinesis.Medium;
using Telekinesis.Medium.Blazor;
using Xunit;

namespace Telekinesis.Medium.Blazor.Tests;

public class MediumManifestBuilderTests
{
    private static MediumElement Elm(string id, string role = "button", string? name = null) =>
        new() { SemanticId = id, Role = role, Name = name };

    [Fact]
    public void Builds_globals_and_views()
    {
        var b = new MediumManifestBuilder { Application = "AcmeERP" };
        b.Register(Elm("app.title", "text", "Title"));
        b.RegisterView("InvoiceEditor", Elm("invoice.create", "button", "Create Invoice"));
        b.RegisterView("InvoiceEditor", Elm("invoice.customer", "textbox", "Customer"));

        var m = b.Build();
        Assert.Equal("AcmeERP", m.Application);
        Assert.Single(m.Elements);                        // app.title
        Assert.Equal("app.title", m.Elements[0].SemanticId);
        Assert.Equal(2, m.Views["InvoiceEditor"].Elements.Count);
    }

    [Fact]
    public void Last_registration_for_an_id_wins_within_a_scope()
    {
        var b = new MediumManifestBuilder();
        b.Register(Elm("invoice.create", "button", "Create"));
        b.Register(Elm("invoice.create", "button", "Create Invoice"));
        var m = b.Build();
        Assert.Equal("Create Invoice", m.Elements.Single().Name);   // replaced
    }

    [Fact]
    public void A_view_id_takes_precedence_over_the_same_global_id()
    {
        var b = new MediumManifestBuilder();
        b.Register(Elm("invoice.create", "button", "Global Create"));
        b.RegisterView("V", Elm("invoice.create", "button", "View Create"));
        var m = b.Build();
        Assert.Empty(m.Elements);                          // not in globals
        Assert.Equal("View Create", m.Views["V"].Elements.Single().Name);
    }

    [Fact]
    public void Rejects_blank_semantic_ids()
    {
        var b = new MediumManifestBuilder();
        Assert.False(b.Register(Elm("")));
        Assert.False(b.RegisterView("V", Elm("  ")));
        Assert.Empty(b.Build().Elements);
    }
}
