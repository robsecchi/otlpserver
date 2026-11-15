using Google.Rpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using OtlpServer.Logs.Validation;
using OtlpServer.Tests.Infrastructure;

namespace OtlpServer.Tests.Grpc;

/// <summary>
/// Requests where the WHOLE export fails. Spec: OTLP/gRPC "Failures" (retryable vs not).
/// </summary>
public sealed class ExportFailureTests
{
    [Fact]
    public async Task All_records_rejected_is_invalid_argument_with_bad_request_details()
    {
        await using var server = OtlpTestServer.Start();

        var ex = await Assert.ThrowsAsync<RpcException>(() => server.Client.ExportAsync(
            Requests.WithRecords(Requests.RecordWithDuplicateKey(), Requests.RecordWithDuplicateKey())).ResponseAsync);

        // INVALID_ARGUMENT is non-retryable: the client MUST drop the data.
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("2 of 2 log record(s) rejected", ex.Status.Detail);

        // The structured details travel in the grpc-status-details-bin trailer.
        var badRequest = ex.GetRpcStatus()?.GetDetail<BadRequest>();
        Assert.NotNull(badRequest);
        var violation = Assert.Single(badRequest.FieldViolations);
        Assert.Equal("resource_logs[0].scope_logs[0].log_records[0]", violation.Field);
        Assert.Contains("duplicate_attribute_key (x2)", violation.Description);
    }

    [Fact]
    public async Task Unexpected_server_error_is_internal_and_hides_exception_details()
    {
        await using var server = OtlpTestServer.Start(services: s =>
            s.AddSingleton<ILogRecordValidator>(new ThrowingValidator("secret internal detail")));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(Requests.WithRecords(Requests.ValidRecord())).ResponseAsync);

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.DoesNotContain("secret internal detail", ex.Status.Detail);
        Assert.Null(ex.GetRpcStatus()?.GetDetail<RetryInfo>());
    }
}
