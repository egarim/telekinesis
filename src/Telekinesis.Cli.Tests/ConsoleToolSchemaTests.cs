using System.Text.Json;
using ModelContextProtocol.Server;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// The generated MCP schema is the contract a model actually sees, and it is not
/// obvious from the C# signature: the SDK marks a parameter `required` unless it
/// has a C# DEFAULT, and a missing required argument throws rather than binding
/// null. Making `sendEnter` nullable was not enough to fix issue #57 — only
/// `= true` was — and nothing in the build caught that. These tests read the
/// schema so the next person cannot regress it silently.
/// </summary>
public class ConsoleToolSchemaTests
{
    private static JsonElement SchemaOf(Delegate method) =>
        McpServerTool.Create(method).ProtocolTool.InputSchema;

    private static bool IsRequired(JsonElement schema, string name) =>
        schema.TryGetProperty("required", out var required)
        && required.EnumerateArray().Any(x => x.GetString() == name);

    private static JsonElement Property(JsonElement schema, string name) =>
        schema.GetProperty("properties").GetProperty(name);

    [Fact]
    public void sendEnter_is_optional_so_omitting_it_does_not_throw()
        => Assert.False(IsRequired(SchemaOf(ConsoleTools.ConsoleWrite), "sendEnter"),
            "sendEnter must not be `required`: a missing required argument throws, "
            + "which is issue #57 — the command was typed but never submitted.");

    [Fact]
    public void sendEnter_defaults_to_true_in_the_schema()
    {
        var prop = Property(SchemaOf(ConsoleTools.ConsoleWrite), "sendEnter");
        Assert.True(prop.TryGetProperty("default", out var def),
            "sendEnter needs a schema default, or a model has to guess.");
        Assert.True(def.GetBoolean());
    }

    [Fact]
    public void sessionId_and_text_stay_required()
    {
        // The counterpart check: the fix must not have made everything optional.
        var schema = SchemaOf(ConsoleTools.ConsoleWrite);
        Assert.True(IsRequired(schema, "sessionId"));
        Assert.True(IsRequired(schema, "text"));
    }

    [Fact]
    public void console_open_dimensions_are_documented_as_bounded()
    {
        // cols/rows are clamped to TerminalScreen's range (issue #58); the caller
        // learns the bound from the description, so it must say so.
        var schema = SchemaOf(ConsoleTools.ConsoleOpen);
        foreach (var name in new[] { "cols", "rows" })
        {
            var desc = Property(schema, name).GetProperty("description").GetString();
            Assert.Contains("max 1000", desc);
        }
    }
}
