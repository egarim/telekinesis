using System.Text.Json;
using System.Text.Json.Nodes;

namespace Telekinesis.Sentiora;

/// <summary>One JSON-RPC response line decoded from the runtime's stdout.</summary>
internal readonly record struct SentioraResponse(
    long Id,
    bool IsError,
    string? ErrorMessage,
    int ErrorCode,
    JsonNode? Result);

/// <summary>The transport itself is broken (pipe closed, unparseable line). Distinct
/// from <see cref="SentioraProtocolException"/>, which is the runtime answering
/// correctly with an error.
///
/// <para>Deliberately carries NO copy of the offending line. A line that fails to parse
/// cannot be inspected for secrets, and the one response that does carry a secret —
/// terminal.create's, which returns the owner token — is exactly the kind of long line
/// that could get truncated mid-token. These messages reach stderr and issue #67
/// requires an audit of secret logging, so the diagnostic is which framing rule was
/// violated rather than the payload that violated it.</para></summary>
internal sealed class SentioraTransportException(string message) : Exception(message);

/// <summary>
/// Newline-delimited JSON-RPC 2.0 framing (issue #67). Every line on the runtime's
/// stdout must decode to a conforming response; a line that does not is a FATAL
/// transport error rather than something to skip, because a skipped line means a
/// request that will never be answered and a stream that may already be corrupted.
/// (The runtime was adversarially verified to emit zero non-conforming lines even
/// when its child prints forged JSON-RPC and invalid UTF-8 — being strict here
/// costs nothing and catches the case where that stops being true.)
/// </summary>
internal static class SentioraFraming
{
    public static SentioraResponse ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            throw new SentioraTransportException("The Sentiora runtime sent a blank line.");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new SentioraTransportException(
                $"The Sentiora runtime sent a line that is not JSON ({ex.Message}).");
        }

        if (node is not JsonObject obj)
            throw new SentioraTransportException("The Sentiora runtime sent a JSON value that is not an object.");

        if (!obj.ContainsKey("jsonrpc") || !TryGetString(obj, "jsonrpc", out var version) || version != "2.0")
            throw new SentioraTransportException("The Sentiora runtime sent a response without \"jsonrpc\":\"2.0\".");

        if (!TryGetId(obj, out var id))
            throw new SentioraTransportException("The Sentiora runtime sent a response without a numeric \"id\".");

        var hasResult = obj.ContainsKey("result");
        var hasError = obj.ContainsKey("error");
        if (hasResult == hasError)
            throw new SentioraTransportException(
                $"The Sentiora runtime sent a response with {(hasResult ? "both" : "neither")} \"result\" and \"error\".");

        if (hasError)
        {
            if (obj["error"] is not JsonObject err)
                throw new SentioraTransportException("The Sentiora runtime sent a non-object \"error\".");
            TryGetString(err, "message", out var message);
            var code = err["code"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : 0;
            return new SentioraResponse(id, true, message ?? string.Empty, code, null);
        }

        return new SentioraResponse(id, false, null, 0, obj["result"]);
    }

    /// <summary>Build a request line. The trailing newline is the frame delimiter.</summary>
    public static string FormatRequest(long id, string method, JsonObject? parameters)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) request["params"] = parameters;
        return request.ToJsonString() + "\n";
    }

    private static bool TryGetString(JsonObject obj, string name, out string? value)
    {
        value = obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return value is not null;
    }

    private static bool TryGetId(JsonObject obj, out long id)
    {
        id = 0;
        return obj["id"] is JsonValue v && v.TryGetValue<long>(out id);
    }
}
