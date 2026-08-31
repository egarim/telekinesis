using Telekinesis.Medium;
using Xunit;

namespace Telekinesis.Medium.Tests;

public class ElementDefaultsTests
{
    [Fact]
    public void Defaults_are_safe_and_non_presumptuous()
    {
        var element = new MediumElement { SemanticId = "x", Role = "button" };

        Assert.Equal(MediumRisk.Unknown, element.Risk);     // never guessed safe
        Assert.False(element.RequiresConfirmation);
        Assert.Empty(element.Actions);
        Assert.Empty(element.Relationships);
        Assert.Empty(element.Metadata);
        Assert.Null(element.Name);
        Assert.Null(element.Intent);
    }

    [Fact]
    public void Aggregates_are_mutable_after_construction()
    {
        // The model is shaped as immutable records, but collections are passed by
        // reference so an adapter can build them with collection expressions.
        var element = new MediumElement
        {
            SemanticId = "x",
            Role = "button",
            Actions = ["invoke", "click"],
            Metadata = new Dictionary<string, object?> { ["kind"] = "primary" },
        };

        Assert.Equal(2, element.Actions.Count);
        Assert.Equal("primary", element.Metadata["kind"]);
    }

    [Fact]
    public void Destructive_and_confirmation_metadata_carry_through()
    {
        var element = new MediumElement
        {
            SemanticId = "invoice.delete",
            Role = "button",
            Intent = "invoice.delete",
            Risk = MediumRisk.Destructive,
            RequiresConfirmation = true,
        };

        Assert.Equal(MediumRisk.Destructive, element.Risk);
        Assert.True(element.RequiresConfirmation);
    }
}
