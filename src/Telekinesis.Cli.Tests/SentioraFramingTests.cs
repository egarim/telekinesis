using Telekinesis.Sentiora;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// JSON-RPC framing (issue #67). The transport reads the runtime's stdout line by line
/// and MUST treat a non-conforming line as fatal rather than skipping it: a skipped
/// line is a request that will never be answered, and a stream that has started
/// producing garbage is a stream that may already be corrupted — which is the whole
/// failure mode #46 exists to prevent. The real runtime was adversarially verified to
/// emit zero non-conforming lines even when its child prints forged JSON-RPC and
/// invalid UTF-8, so strictness here costs nothing.
/// </summary>
public class SentioraFramingTests
{
    [Fact]
    public void A_result_line_parses_into_id_and_result()
    {
        var response = SentioraFraming.ParseLine("""{"jsonrpc":"2.0","id":7,"result":{"revision":3}}""");
        Assert.Equal(7, response.Id);
        Assert.False(response.IsError);
        Assert.Equal(3, response.Result!["revision"]!.GetValue<int>());
    }

    [Fact]
    public void An_error_line_parses_into_message_and_the_shared_code()
    {
        var response = SentioraFraming.ParseLine(
            """{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"Stale observation"}}""");
        Assert.True(response.IsError);
        Assert.Equal("Stale observation", response.ErrorMessage);
        // Every Sentiora error shares -32000, so the message is the only discriminator.
        Assert.Equal(-32000, response.ErrorCode);
        Assert.Null(response.Result);
    }

    [Fact]
    public void A_response_to_an_abandoned_id_still_parses()
    {
        // Dropping an unmatched id is the caller's decision, not a framing failure —
        // a cancelled or timed-out request still gets answered by the runtime.
        var response = SentioraFraming.ParseLine("""{"jsonrpc":"2.0","id":999,"result":true}""");
        Assert.Equal(999, response.Id);
        Assert.False(response.IsError);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"jsonrpc\":\"2.0\"")]                       // truncated
    [InlineData("")]                                            // blank
    [InlineData("   ")]                                         // whitespace only
    [InlineData("[1,2,3]")]                                     // array, not object
    [InlineData("\"a string\"")]                                // scalar, not object
    [InlineData("""{"id":1,"result":null}""")]                  // no jsonrpc
    [InlineData("""{"jsonrpc":"1.0","id":1,"result":null}""")]  // wrong version
    [InlineData("""{"jsonrpc":"2.0","result":null}""")]         // no id
    [InlineData("""{"jsonrpc":"2.0","id":"abc","result":null}""")]  // non-numeric id
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]                // neither result nor error
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":1,"error":{"code":-32000,"message":"x"}}""")] // both
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":"oops"}""")] // error not an object
    public void Anything_that_is_not_a_conforming_response_is_a_fatal_transport_error(string line)
        => Assert.Throws<SentioraTransportException>(() => SentioraFraming.ParseLine(line));

    [Fact]
    public void A_forged_response_that_happens_to_be_well_formed_is_not_a_framing_error()
    {
        // A child that manages to put a valid JSON-RPC line on the host's stdout would
        // parse cleanly — framing cannot defend against that, correlation by id can.
        // This pins the boundary so nobody mistakes strict parsing for authentication.
        var response = SentioraFraming.ParseLine("""{"jsonrpc":"2.0","id":1,"result":{"HIJACKED":true}}""");
        Assert.False(response.IsError);
        Assert.Equal(1, response.Id);
    }

    [Fact]
    public void A_hostile_line_is_never_echoed_into_the_exception_message()
    {
        // The message reaches stderr, and a line that fails to parse cannot be inspected
        // for secrets — the one response that carries a secret is terminal.create's,
        // which returns the owner token. So the diagnostic is WHICH framing rule was
        // violated, never the payload that violated it (issue #67 secret-logging audit).
        var marker = "TKOWNERSECRETVALUE";
        var ex = Assert.Throws<SentioraTransportException>(
            () => SentioraFraming.ParseLine($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"owner\":\"{marker}\""));

        Assert.DoesNotContain(marker, ex.Message);
        Assert.DoesNotContain(marker, ex.ToString());
        Assert.Contains("not JSON", ex.Message);
    }

    [Fact]
    public void A_multi_megabyte_hostile_line_cannot_inflate_the_exception_message()
    {
        var ex = Assert.Throws<SentioraTransportException>(
            () => SentioraFraming.ParseLine(new string('A', 1_000_000)));
        Assert.True(ex.Message.Length < 200, $"message was {ex.Message.Length} chars");
    }

    [Fact]
    public void A_request_is_one_newline_terminated_line()
    {
        var line = SentioraFraming.FormatRequest(
            4, "terminal.type",
            new System.Text.Json.Nodes.JsonObject { ["sessionId"] = "abc", ["revision"] = 2 });

        Assert.EndsWith("\n", line);
        // Exactly one frame: no embedded newline that would split the request in two.
        Assert.DoesNotContain("\n", line.TrimEnd('\n'));
        Assert.DoesNotContain("\r", line);

        var parsed = System.Text.Json.Nodes.JsonNode.Parse(line)!;
        Assert.Equal("2.0", parsed["jsonrpc"]!.GetValue<string>());
        Assert.Equal("terminal.type", parsed["method"]!.GetValue<string>());
        Assert.Equal(4, parsed["id"]!.GetValue<int>());
        Assert.Equal("abc", parsed["params"]!["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public void A_request_without_parameters_omits_the_params_member()
    {
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(
            SentioraFraming.FormatRequest(1, "initialize", null))!;
        Assert.False(parsed.AsObject().ContainsKey("params"));
    }
}
