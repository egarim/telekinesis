using Telekinesis.Abstractions;
using Telekinesis.Cli;
using Xunit;

namespace Telekinesis.Cli.Tests;

public class OneShotTests
{
    [Theory]
    [InlineData("Save", null, "Save")]
    [InlineData("Button:Save", AccessibleRole.Button, "Save")]
    [InlineData("edit:Enrollment code", AccessibleRole.Edit, "Enrollment code")]
    [InlineData("http://x", null, "http://x")]       // unknown prefix stays part of the name
    [InlineData(":leading", null, ":leading")]
    public void ParseQuery_splits_role_prefix_only_when_it_is_a_role(
        string query, AccessibleRole? role, string name)
    {
        var (r, n) = OneShot.ParseQuery(query);
        Assert.Equal(role, r);
        Assert.Equal(name, n);
    }

    private static AccessibleElement El(string name, ElementState states) => new()
    {
        Ref = new ElementRef(name, "app"),
        Role = AccessibleRole.Button,
        NativeRole = "button",
        Name = name,
        States = states,
    };

    [Fact]
    public void PickBest_prefers_exact_name_then_visible_enabled_then_first()
    {
        var exact = El("Save", ElementState.None);
        var live = El("Save As", ElementState.Visible | ElementState.Enabled);
        var dead = El("Save All", ElementState.None);

        Assert.Same(exact, OneShot.PickBest([dead, live, exact], "save"));
        Assert.Same(live, OneShot.PickBest([dead, live], "sav"));
        Assert.Same(dead, OneShot.PickBest([dead], "sav"));
        Assert.Null(OneShot.PickBest([], "sav"));
    }
}
