using System.Net;
using Google.Rpc;
using Microsoft.Extensions.DependencyInjection;
using OtlpServer.Logs.Validation;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Http;

/// <summary>
/// POST /v1/logs with Content-Type: application/json. Spec: OTLP/HTTP "JSON Protobuf Encoding".
/// </summary>
public sealed class HttpJsonTests
{
    private const string TraceIdHex = "5b8efff798038103d269b633813fc60c"; // 16 bytes
    private const string SpanIdHex = "eee19b7ec3c1b174";                  // 8 bytes

    /// <summary>The usual envelope: one resource, one scope, the given records (as JSON objects).</summary>
    private static string Request(params string[] logRecords) => $$"""
        {
          "resourceLogs": [{
            "resource": { "attributes": [{ "key": "service.name", "value": { "stringValue": "json-tests" } }] },
            "scopeLogs": [{
              "scope": { "name": "tests" },
              "logRecords": [{{string.Join(",", logRecords)}}]
            }]
          }]
        }
        """;

    private const string ValidRecordJson = """
        { "timeUnixNano": "1700000000000000000", "severityNumber": 9, "body": { "stringValue": "hello" } }
        """;

    // ---- Decoding --------------------------------------------------------------------------

    [Theory]
    [InlineData(TraceIdHex, SpanIdHex)]
    [InlineData("5B8EFFF798038103D269B633813FC60C", "EEE19B7EC3C1B174")] // hex is case-insensitive
    public async Task Hex_trace_and_span_ids_are_decoded_as_hex(string traceId, string spanId)
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request($$"""
            { "severityNumber": 9, "traceId": "{{traceId}}", "spanId": "{{spanId}}", "body": { "stringValue": "x" } }
            """));

        // Full success proves the ids were read as 16 + 8 bytes. Read as base64 (the standard
        // proto3 JSON rule), the same strings would give 24 + 12 bytes and a warning.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.ReadJsonExportResponseAsync()).PartialSuccess);
    }

    [Fact]
    public async Task Hex_id_of_the_wrong_length_is_accepted_with_a_warning()
    {
        // Valid hex, wrong length: same outcome as the same bytes in binary protobuf.
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request("""
            { "severityNumber": 9, "traceId": "010203" }
            """));

        var body = await response.ReadJsonExportResponseAsync();
        Assert.Equal(0, body.PartialSuccess.RejectedLogRecords);
        Assert.Contains("invalid_trace_id", body.PartialSuccess.ErrorMessage);
    }

    [Fact]
    public async Task Non_hex_id_is_400_with_its_location()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request(ValidRecordJson, """
            { "severityNumber": 9, "spanId": "not-hex!" }
            """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await response.ReadJsonStatusAsync();
        Assert.Equal((int)Code.InvalidArgument, status.Code);
        Assert.Contains("resource_logs[0].scope_logs[0].log_records[1].spanId must be a hex string", status.Message);
    }

    [Fact]
    public async Task Unknown_fields_are_ignored_at_any_level()
    {
        // "OTLP/JSON receivers MUST ignore message fields with unknown names."
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync("""
            {
              "fromTheFuture": 1,
              "resourceLogs": [{
                "newResourceThing": { "a": [1, 2] },
                "scopeLogs": [{ "logRecords": [{ "severityNumber": 9, "unknownField": "x" }] }]
              }]
            }
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.ReadJsonExportResponseAsync()).PartialSuccess);
    }

    [Theory]
    [InlineData("\"severityNumber\": 9")]                      // integer: what the spec requires
    [InlineData("\"severityNumber\": \"SEVERITY_NUMBER_INFO\"")] // name: accepted too (lenient)
    [InlineData("\"timeUnixNano\": \"1700000000000000000\"")]   // int64 as a string
    [InlineData("\"timeUnixNano\": 1700000000000000000")]       // int64 as a number
    [InlineData("\"time_unix_nano\": \"1700000000000000000\"")] // snake_case key: accepted too (lenient)
    [InlineData("\"trace_id\": \"" + TraceIdHex + "\"")]       // ...also for the hex rule
    public async Task Accepted_value_and_key_forms(string field)
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request($$"""{ {{field}} }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.ReadJsonExportResponseAsync()).PartialSuccess);
    }

    [Fact]
    public async Task Unknown_enum_name_is_ignored_like_an_unknown_field()
    {
        // With unknown fields ignored, proto3 JSON also ignores enum NAMES it doesn't know: the
        // field stays unset (SEVERITY_NUMBER_UNSPECIFIED = 0, which is valid). Same idea as
        // forward compatibility: a newer .proto may add enum values. (Senders should use
        // integers anyway; an unknown NUMBER is kept and rejected, see the next test.)
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request("""{ "severityNumber": "SEVERITY_NUMBER_LOUD" }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.ReadJsonExportResponseAsync()).PartialSuccess);
    }

    [Fact]
    public async Task Out_of_range_severity_number_is_parsed_and_then_rejected_by_validation()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request(ValidRecordJson, """{ "severityNumber": 99 }"""));

        var body = await response.ReadJsonExportResponseAsync();
        Assert.Equal(1, body.PartialSuccess.RejectedLogRecords);
        Assert.Contains("severity_number_out_of_range", body.PartialSuccess.ErrorMessage);
    }

    [Theory]
    [InlineData("")]                                       // empty body: not JSON at all
    [InlineData("{ \"resourceLogs\": [ ")]                 // truncated
    [InlineData("[]")]                                     // JSON, but not an object
    [InlineData("{ \"resourceLogs\": \"oops\" }")]         // wrong type for a known field
    [InlineData("{ \"resourceLogs\": [], \"resourceLogs\": [] }")] // duplicate property name
    [InlineData("{ \"resourceLogs\": [{ \"scopeLogs\": [{ \"logRecords\": [{ \"traceId\": \"00\", \"traceId\": \"01\" }] }] }] }")]
    public async Task Undecodable_json_is_400(string json)
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await response.ReadJsonStatusAsync();
        Assert.Equal((int)Code.InvalidArgument, status.Code);
        Assert.Contains("not a valid OTLP/JSON ExportLogsServiceRequest", status.Message);
    }

    [Fact]
    public async Task Nesting_deeper_than_the_parser_limit_is_400()
    {
        // Same protection as binary protobuf: Google.Protobuf's 100-message-level limit.
        var value = "{ \"stringValue\": \"bottom\" }";
        for (var i = 0; i < 60; i++)
        {
            value = $$"""{ "kvlistValue": { "values": [{ "key": "k", "value": {{value}} }] } }""";
        }
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request($$"""{ "body": {{value}} }"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Leading_byte_order_mark_is_ignored()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync("﻿" + Request(ValidRecordJson));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Gzip_json_is_accepted()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request(ValidRecordJson), gzip: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Responses are JSON too --------------------------------------------------------------

    [Fact]
    public async Task Full_success_body_is_an_empty_json_object()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request(ValidRecordJson));

        Assert.Equal(OtlpHttpClientExtensions.Json, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{ }", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Partial_success_body_uses_lower_camel_case_names()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request(ValidRecordJson, """
            { "attributes": [{ "key": "k", "value": {} }, { "key": "k", "value": {} }] }
            """));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"partialSuccess\"", raw);
        Assert.Contains("\"rejectedLogRecords\"", raw);
        Assert.Contains("\"errorMessage\"", raw);
    }

    [Fact]
    public async Task All_rejected_is_400_with_a_json_status_and_bad_request_details()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsJsonAsync(Request("""{ "severityNumber": 99 }"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await response.ReadJsonStatusAsync();
        var violation = Assert.Single(status.GetDetail<BadRequest>()!.FieldViolations);
        Assert.Equal("resource_logs[0].scope_logs[0].log_records[0]", violation.Field);
    }

    [Fact]
    public async Task Throttled_is_503_with_retry_after_and_a_json_status()
    {
        var gate = new GatedValidator();
        await using var server = OtlpTestServer.Start(
            new Dictionary<string, string?> { ["Otlp:Logs:MaxConcurrentExports"] = "1" },
            s => s.AddSingleton<ILogRecordValidator>(gate));

        var first = server.Http.PostLogsJsonAsync(Request(ValidRecordJson));
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)));

        using var throttled = await server.Http.PostLogsJsonAsync(Request(ValidRecordJson));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
        var status = await throttled.ReadJsonStatusAsync();
        Assert.NotNull(status.GetDetail<RetryInfo>()); // the Any detail survives JSON formatting

        gate.Release.Set();
        (await first).Dispose();
    }

    [Fact]
    public async Task Not_found_with_a_json_request_gets_a_json_status()
    {
        await using var server = OtlpTestServer.Start();

        var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await server.Http.PostAsync("/v1/traces", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("POST /v1/logs", (await response.ReadJsonStatusAsync()).Message);
    }

    // ---- Same data, both encodings --------------------------------------------------------------

    [Fact]
    public async Task Json_and_protobuf_requests_with_the_same_data_get_the_same_outcome()
    {
        await using var server = OtlpTestServer.Start();

        // Same three records in both encodings: one valid, one short trace id, one duplicate key.
        var protobufRequest = WithRecords(ValidRecord(), RecordWithShortTraceId(), RecordWithDuplicateKey());
        var json = Request(
            """{ "timeUnixNano": "1700000000000000000", "severityNumber": 9, "body": { "stringValue": "hello" }, "attributes": [{ "key": "k", "value": { "stringValue": "v" } }] }""",
            """{ "timeUnixNano": "1700000000000000000", "severityNumber": 9, "body": { "stringValue": "short trace id" }, "attributes": [{ "key": "k", "value": { "stringValue": "v" } }], "traceId": "010203" }""",
            """{ "timeUnixNano": "1700000000000000000", "severityNumber": 9, "body": { "stringValue": "duplicate key" }, "attributes": [{ "key": "k", "value": { "stringValue": "v" } }, { "key": "k", "value": { "stringValue": "again" } }] }""");

        using var viaProtobuf = await server.Http.PostLogsAsync(protobufRequest);
        using var viaJson = await server.Http.PostLogsJsonAsync(json);

        Assert.Equal(await viaProtobuf.ReadExportResponseAsync(), await viaJson.ReadJsonExportResponseAsync());
    }
}
