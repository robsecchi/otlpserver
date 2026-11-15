using System.Net;
using Google.Protobuf;
using Google.Rpc;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpServer.Logs.Validation;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Http;

/// <summary>
/// POST /v1/logs with binary protobuf. Spec: OTLP/HTTP "Binary Protobuf Encoding",
/// "Full Success", "Partial Success", "Failures". Same scenarios as the gRPC tests, with
/// HTTP's status codes and Status bodies.
/// </summary>
public sealed class HttpExportTests
{
    [Fact]
    public async Task Valid_records_get_200_with_partial_success_unset()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsAsync(WithRecords(ValidRecord(), ValidRecord()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The response uses the same Content-Type as the request.
        var body = await response.ReadExportResponseAsync();
        Assert.Null(body.PartialSuccess);
    }

    [Fact]
    public async Task Empty_body_is_an_empty_request_and_succeeds()
    {
        // Zero bytes is a valid protobuf encoding of a message with no fields set.
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsAsync([]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.ReadExportResponseAsync()).PartialSuccess);
    }

    [Fact]
    public async Task Partial_success_is_still_200()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsAsync(
            WithRecords(ValidRecord(), RecordWithDuplicateKey()));

        // Not an error: the client MUST NOT retry, so it can't be a 4xx/5xx.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadExportResponseAsync();
        Assert.Equal(1, body.PartialSuccess.RejectedLogRecords);
        Assert.Contains("duplicate_attribute_key", body.PartialSuccess.ErrorMessage);
    }

    [Fact]
    public async Task All_records_rejected_is_400_with_status_and_bad_request()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsAsync(WithRecords(RecordWithDuplicateKey()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await response.ReadStatusAsync();
        Assert.Equal((int)Code.InvalidArgument, status.Code);
        Assert.Contains("1 of 1 log record(s) rejected", status.Message);
        var violation = Assert.Single(status.GetDetail<BadRequest>()!.FieldViolations);
        Assert.Equal("resource_logs[0].scope_logs[0].log_records[0]", violation.Field);
    }

    [Theory]
    [InlineData("0A64AABB")] // truncated
    [InlineData("0F")]       // invalid wire type
    public async Task Undecodable_body_is_400(string hex)
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsAsync(Convert.FromHexString(hex));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await response.ReadStatusAsync();
        Assert.Equal((int)Code.InvalidArgument, status.Code);
        Assert.Contains("not a valid ExportLogsServiceRequest", status.Message);
        // Decoding errors carry no per-field violations, so no empty BadRequest either.
        Assert.Empty(status.Details);
    }

    [Fact]
    public async Task Unknown_fields_are_ignored()
    {
        await using var server = OtlpTestServer.Start();

        var bytes = WithRecords(ValidRecord()).ToByteArray().Concat(Convert.FromHexString("F80601")).ToArray();
        using var response = await server.Http.PostLogsAsync(bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unexpected_server_error_is_500_and_hides_exception_details()
    {
        await using var server = OtlpTestServer.Start(services: s =>
            s.AddSingleton<ILogRecordValidator>(new ThrowingValidator("secret internal detail")));

        using var response = await server.Http.PostLogsAsync(WithRecords(ValidRecord()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var status = await response.ReadStatusAsync();
        Assert.Equal((int)Code.Internal, status.Code);
        Assert.DoesNotContain("secret internal detail", status.Message);
    }

    [Fact]
    public async Task Same_request_gets_the_same_outcome_over_grpc_and_http()
    {
        // Both transports call the same core handler; only the framing differs.
        await using var server = OtlpTestServer.Start();
        var request = WithRecords(ValidRecord(), RecordWithShortTraceId(), RecordWithDuplicateKey());

        var viaGrpc = await server.Client.ExportAsync(request);
        using var httpResponse = await server.Http.PostLogsAsync(request);
        var viaHttp = await httpResponse.ReadExportResponseAsync();

        Assert.Equal(viaGrpc, viaHttp);
    }
}
