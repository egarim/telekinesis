using Telekinesis.Medium.Blazor;
using Xunit;

namespace Telekinesis.Medium.Blazor.Tests;

public class MediumDomMapperTests
{
    [Theory]
    [InlineData("button", null, "button")]
    [InlineData("a", null, "link")]
    [InlineData("input", "text", "textbox")]
    [InlineData("input", "checkbox", "checkbox")]
    [InlineData("input", "radio", "radio")]
    [InlineData("select", null, "combobox")]
    [InlineData("textarea", null, "textbox")]
    [InlineData("label", null, "label")]
    [InlineData("img", null, "image")]
    public void Maps_common_control_roles(string tag, string? type, string expected)
        => Assert.Equal(expected, MediumDomMapper.MapRole(tag, type));

    [Fact]
    public void Maps_accessible_name_from_aria_label()
    {
        var e = MediumDomMapper.Map("button", new Dictionary<string, string> { ["aria-label"] = "Create Invoice" });
        Assert.Equal("Create Invoice", e.Name);
        Assert.Equal("button", e.Role);
    }

    [Fact]
    public void Carries_semantic_intent_from_medium_attribute()
    {
        var e = MediumDomMapper.Map("button", new Dictionary<string, string>
        {
            ["aria-label"] = "Delete Invoice",
            ["data-medium-intent"] = "invoice.delete",
        });
        Assert.Equal("invoice.delete", e.Intent);
        Assert.Equal("button.delete.invoice", e.SemanticId);   // derived deterministically from role+name
    }

    [Fact]
    public void Derives_deterministic_semantic_id_when_not_supplied()
    {
        var a = MediumDomMapper.Map("button", new Dictionary<string, string> { ["aria-label"] = "Create Invoice" });
        var b = MediumDomMapper.Map("button", new Dictionary<string, string> { ["aria-label"] = "Create Invoice" });
        Assert.Equal(a.SemanticId, b.SemanticId);              // deterministic
        Assert.Equal("button.create.invoice", a.SemanticId);
    }

    [Fact]
    public void Explicit_semantic_id_wins()
    {
        var e = MediumDomMapper.Map("button", new Dictionary<string, string> { ["aria-label"] = "Create Invoice" }, semanticId: "invoice.create");
        Assert.Equal("invoice.create", e.SemanticId);
    }
}
