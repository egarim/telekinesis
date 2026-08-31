using Telekinesis.Medium;
using Xunit;

namespace Telekinesis.Medium.Tests;

public class SemanticIdTests
{
    [Theory]
    [InlineData("invoice.customer")]
    [InlineData("invoice.create")]
    [InlineData("navigation.settings")]
    [InlineData("form.payment.card-number")]
    public void Canonical_ids_round_trip_unchanged(string id)
    {
        Assert.True(MediumSemanticId.TryNormalize(id, out var norm, out var error), error);
        Assert.Equal(id, norm);
        Assert.True(MediumSemanticId.IsValid(id));
        Assert.Equal(id, MediumSemanticId.Normalize(id));
    }

    [Theory]
    [InlineData("Create Invoice", "create.invoice")]
    [InlineData("  Nav*Settings  ", "nav.settings")]
    [InlineData("Invoice", "invoice")]
    [InlineData("Delete Customer!", "delete.customer")]
    [InlineData("checkout-step2", "checkout-step2")]
    public void Normalizes_deterministically(string raw, string expected)
    {
        Assert.True(MediumSemanticId.TryNormalize(raw, out var norm, out var error), error);
        Assert.Equal(expected, norm);
    }

    [Fact]
    public void Normalization_is_deterministic_across_calls()
    {
        foreach (var _ in Enumerable.Range(0, 5))
            Assert.Equal("create.invoice", MediumSemanticId.Normalize("Create Invoice"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("***")]
    [InlineData("   .   ")]
    public void Empty_or_non_usable_are_rejected(string raw)
    {
        Assert.False(MediumSemanticId.TryNormalize(raw, out var norm, out var error));
        Assert.Null(norm);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.False(MediumSemanticId.IsValid(raw));
        Assert.Null(MediumSemanticId.Normalize(raw));
    }

    [Fact]
    public void Mixed_case_and_stray_separators_are_not_canonical()
    {
        // Mixed case is valid after normalize but not valid as-is.
        Assert.True(MediumSemanticId.TryNormalize("Invoice.Customer", out var norm, out _));
        Assert.Equal("invoice.customer", norm);
        Assert.False(MediumSemanticId.IsValid("Invoice.Customer"));
    }

    [Fact]
    public void Require_throws_on_unusable()
    {
        Assert.Throws<ArgumentException>(() => MediumSemanticId.Require("   "));
        Assert.Equal("invoice.create", MediumSemanticId.Require("invoice.create"));
    }
}
