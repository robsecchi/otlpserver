using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Rpc;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport;

/// <summary>
/// OTLP/JSON: the JSON encoding of the OTLP messages, used by OTLP/HTTP with
/// <c>Content-Type: application/json</c>.
///
/// <para>
/// It is the standard proto3 JSON mapping, which Google.Protobuf implements
/// (<see cref="JsonParser"/> / <see cref="JsonFormatter"/>), with these differences from the spec:
/// </para>
/// <list type="bullet">
///   <item><b>traceId / spanId are hex strings</b> (case-insensitive), not base64 like other
///         bytes fields. The standard parser would read them as base64, and since every hex
///         character is also a base64 character, it would silently decode a 32-char hex
///         trace id into 24 wrong bytes. So those two fields are rewritten hex → base64 first.
///         Other bytes fields (e.g. AnyValue.bytesValue) stay base64.</item>
///   <item><b>Unknown fields MUST be ignored</b> (forward compatibility), while the standard
///         parser rejects them by default.</item>
///   <item>Enums MUST be sent as integers. We also accept names; being lenient costs nothing.</item>
///   <item>Keys MUST be lowerCamelCase ("traceId"). The parser also accepts the original
///         snake_case names ("trace_id"); we keep that leniency, like the Collector does.</item>
///   <item>int64 values may be JSON numbers or decimal strings; the parser takes both.</item>
/// </list>
/// <code>
///  UTF-8 body ──► System.Text.Json DOM ──► traceId/spanId: hex → base64 ──► Google JsonParser ──► ExportLogsServiceRequest
/// </code>
/// <para>
/// This parses the body twice (DOM, then Google's parser). It's simpler and safer than
/// writing a full OTLP JSON parser, and the body size is already capped.
/// </para>
/// </summary>
public static class OtlpJsonCodec
{
    // Google.Protobuf enforces its own message nesting limit (100, as for binary). This is only
    // the JSON reader's limit: generous, since one message level takes about two JSON levels
    // (object + array). Its default (64) would refuse legitimately nested AnyValues first.
    private const int MaxJsonDepth = 256;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = MaxJsonDepth,
        // The same property twice in one object, e.g. { "traceId": "a", "traceId": "b" }.
        // RFC 8259 leaves the meaning undefined and parsers disagree (first wins, last wins,
        // error), so the request is refused rather than guessing. (Not to be confused with
        // duplicate ATTRIBUTE keys: those are array entries, handled by validation.)
        AllowDuplicateProperties = false,
    };

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    // Error bodies are google.rpc.Status, whose details are Any. Formatting an Any as JSON needs
    // to know the message types it may contain.
    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default
        .WithTypeRegistry(TypeRegistry.FromMessages(BadRequest.Descriptor, RetryInfo.Descriptor)));

    /// <exception cref="OtlpExportException">InvalidData (HTTP 400) if the body can't be decoded.</exception>
    public static ExportLogsServiceRequest ParseLogsRequest(ReadOnlySpan<byte> utf8Json)
    {
        // RFC 8259: senders MUST NOT add a byte order mark, but parsers MAY ignore one. We do.
        if (utf8Json.StartsWith(Utf8Bom))
        {
            utf8Json = utf8Json[Utf8Bom.Length..];
        }

        try
        {
            var root = JsonNode.Parse(utf8Json, documentOptions: DocumentOptions);
            if (root is not JsonObject request)
            {
                throw Invalid("the body must be a JSON object (an ExportLogsServiceRequest)");
            }

            RewriteIdsAsBase64(request);
            return Parser.Parse<ExportLogsServiceRequest>(request.ToJsonString());
        }
        catch (Exception ex) when (ex is JsonException or InvalidJsonException or InvalidProtocolBufferException)
        {
            // Malformed JSON, too deep, or a value of the wrong type (e.g. a string where an
            // object belongs). The messages describe the encoding only, so they're safe to pass on.
            throw Invalid(ex.Message);
        }
    }

    /// <summary>Formats a response or a Status as UTF-8 JSON (lowerCamelCase, default values omitted).</summary>
    public static byte[] Format(IMessage message) => Encoding.UTF8.GetBytes(Formatter.Format(message));

    /// <summary>
    /// The only bytes fields with the hex rule in a logs request are LogRecord.traceId and
    /// LogRecord.spanId, at resourceLogs[].scopeLogs[].logRecords[]. Both key spellings are
    /// handled, since the parser accepts both.
    /// </summary>
    private static void RewriteIdsAsBase64(JsonObject request)
    {
        foreach (var (r, resourceLogs) in Items(request, "resourceLogs", "resource_logs"))
        foreach (var (s, scopeLogs) in Items(resourceLogs, "scopeLogs", "scope_logs"))
        foreach (var (l, logRecord) in Items(scopeLogs, "logRecords", "log_records"))
        {
            var location = $"resource_logs[{r}].scope_logs[{s}].log_records[{l}]";
            foreach (var key in (string[])["traceId", "trace_id", "spanId", "span_id"])
            {
                HexToBase64(logRecord, key, location);
            }
        }
    }

    private static void HexToBase64(JsonObject record, string key, string location)
    {
        // Missing, null or not a string: leave it to the parser (null = unset; anything else
        // becomes a clear type error there).
        if (record[key] is not JsonValue value || !value.TryGetValue<string>(out var hex))
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex); // case-insensitive; "" → no bytes (= unset)
        }
        catch (FormatException)
        {
            // Invalid hex is an ENCODING error (like invalid protobuf bytes), so the whole
            // request is refused. A wrong LENGTH is still valid hex: that one passes, and the
            // validator warns about it, as it does for binary protobuf.
            throw Invalid($"{location}.{key} must be a hex string, got \"{Truncate(hex)}\"");
        }

        record[key] = Convert.ToBase64String(bytes);
    }

    /// <summary>The objects in an array field, with their index. Anything unexpected is skipped (the parser reports it).</summary>
    private static IEnumerable<(int Index, JsonObject Item)> Items(JsonObject parent, string camelCase, string snakeCase)
    {
        var array = parent[camelCase] as JsonArray ?? parent[snakeCase] as JsonArray;
        if (array is null) yield break;

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject item) yield return (i, item);
        }
    }

    private static OtlpExportException Invalid(string reason) =>
        OtlpExportException.InvalidData($"Request body is not a valid OTLP/JSON ExportLogsServiceRequest: {reason}", violations: []);

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "...";
}
