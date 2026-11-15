using Google.Protobuf;
using Grpc.Core;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OtlpServer.Tests.Infrastructure;

namespace OtlpServer.Tests.Grpc;

/// <summary>
/// Request bytes that can't be decoded as an ExportLogsServiceRequest.
/// Spec: OTLP/gRPC "Failures": non-retryable errors SHOULD use INVALID_ARGUMENT.
/// </summary>
public sealed class MalformedRequestTests
{
    [Theory]
    // field 1 (resource_logs), wire type 2 (length-delimited), says 100 bytes follow, only 2 do
    [InlineData("0A64AABB", "truncated message")]
    // field 1 with wire type 7, which doesn't exist in protobuf
    [InlineData("0F", "invalid wire type")]
    // tag 0 is never valid (field numbers start at 1)
    [InlineData("0000", "field number zero")]
    public async Task Undecodable_bytes_are_invalid_argument(string hex, string why)
    {
        await using var server = OtlpTestServer.Start();

        var ex = await Assert.ThrowsAsync<RpcException>(() => server.ExportRawAsync(Convert.FromHexString(hex)));

        Assert.True(ex.StatusCode == StatusCode.InvalidArgument, $"{why}: got {ex.StatusCode} ({ex.Status.Detail})");
        Assert.Contains("not a valid ExportLogsServiceRequest", ex.Status.Detail);
    }

    [Fact]
    public async Task Nesting_deeper_than_the_parser_limit_is_invalid_argument()
    {
        // AnyValue -> KeyValueList -> KeyValue -> AnyValue -> ... : 3 message levels per hop.
        // 60 hops = 180 levels, over Google.Protobuf's default recursion limit of 100.
        // The limit exists so a tiny hostile payload can't blow the parser's stack.
        var value = new AnyValue { StringValue = "bottom" };
        for (var i = 0; i < 60; i++)
        {
            value = new AnyValue { KvlistValue = new KeyValueList { Values = { new KeyValue { Key = "k", Value = value } } } };
        }
        var record = Requests.ValidRecord();
        record.Body = value;

        await using var server = OtlpTestServer.Start();

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            server.ExportRawAsync(Requests.WithRecords(record).ToByteArray()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Valid_bytes_sent_raw_are_accepted()
    {
        // Sanity check for the raw path itself: valid bytes must still succeed.
        await using var server = OtlpTestServer.Start();

        var responseBytes = await server.ExportRawAsync(Requests.WithRecords(Requests.ValidRecord()).ToByteArray());

        var response = ExportLogsServiceResponse.Parser.ParseFrom(responseBytes);
        Assert.Null(response.PartialSuccess);
    }

    [Fact]
    public async Task Unknown_fields_are_ignored()
    {
        // Forward compatibility: a newer client may send fields this server's .proto doesn't
        // know yet. Protobuf skips them, so the request must still succeed.
        await using var server = OtlpTestServer.Start();

        var bytes = Requests.WithRecords(Requests.ValidRecord()).ToByteArray()
            .Concat(Convert.FromHexString("F806" + "01")) // field 111, varint, value 1
            .ToArray();

        var response = ExportLogsServiceResponse.Parser.ParseFrom(await server.ExportRawAsync(bytes));
        Assert.Null(response.PartialSuccess);
    }
}
