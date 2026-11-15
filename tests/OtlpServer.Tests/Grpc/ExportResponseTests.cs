using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Logs.V1;
using OtlpServer.Tests.Infrastructure;

namespace OtlpServer.Tests.Grpc;

/// <summary>
/// Requests that end with status OK. Spec: OTLP "Full Success" and "Partial Success".
/// </summary>
public sealed class ExportResponseTests
{
    [Fact]
    public async Task Valid_records_get_full_success_with_partial_success_unset()
    {
        await using var server = OtlpTestServer.Start();

        var response = await server.Client.ExportAsync(
            Requests.WithRecords(Requests.ValidRecord("a"), Requests.ValidRecord("b")));

        // Full Success: "the server MUST leave the partial_success field unset".
        Assert.Null(response.PartialSuccess);
    }

    [Fact]
    public async Task Empty_request_is_a_full_success()
    {
        await using var server = OtlpTestServer.Start();

        // An empty request carries no telemetry; the spec says the server SHOULD answer success.
        var response = await server.Client.ExportAsync(new ExportLogsServiceRequest());

        Assert.Null(response.PartialSuccess);
    }

    [Fact]
    public async Task Resource_and_scope_without_records_is_a_full_success()
    {
        await using var server = OtlpTestServer.Start();

        var request = new ExportLogsServiceRequest
        {
            ResourceLogs = { new ResourceLogs { ScopeLogs = { new ScopeLogs() } } },
        };

        var response = await server.Client.ExportAsync(request);

        Assert.Null(response.PartialSuccess);
    }

    [Fact]
    public async Task Some_records_rejected_gives_partial_success_with_count_and_location()
    {
        await using var server = OtlpTestServer.Start();

        var response = await server.Client.ExportAsync(Requests.WithRecords(
            Requests.ValidRecord(),
            Requests.RecordWithDuplicateKey(),
            Requests.ValidRecord()));

        Assert.NotNull(response.PartialSuccess);
        Assert.Equal(1, response.PartialSuccess.RejectedLogRecords);
        Assert.Contains("duplicate_attribute_key", response.PartialSuccess.ErrorMessage);
        Assert.Contains("resource_logs[0].scope_logs[0].log_records[1]", response.PartialSuccess.ErrorMessage);
    }

    [Fact]
    public async Task Warnings_only_give_partial_success_with_zero_rejected_and_a_message()
    {
        await using var server = OtlpTestServer.Start();

        var response = await server.Client.ExportAsync(
            Requests.WithRecords(Requests.ValidRecord(), Requests.RecordWithShortTraceId()));

        // rejected = 0 is allowed only with a non-empty error_message (otherwise it would
        // mean the same as Full Success).
        Assert.NotNull(response.PartialSuccess);
        Assert.Equal(0, response.PartialSuccess.RejectedLogRecords);
        Assert.Contains("invalid_trace_id", response.PartialSuccess.ErrorMessage);
    }

    [Fact]
    public async Task Error_message_lists_each_rule_once_with_its_count()
    {
        await using var server = OtlpTestServer.Start();

        var records = Enumerable.Range(0, 10).Select(_ => Requests.RecordWithDuplicateKey())
            .Append(Requests.ValidRecord())
            .ToArray();

        var response = await server.Client.ExportAsync(Requests.WithRecords(records));

        Assert.Equal(10, response.PartialSuccess.RejectedLogRecords);
        var message = response.PartialSuccess.ErrorMessage;
        Assert.Contains("duplicate_attribute_key x10", message);
        // Only the first example's location, so the message stays short for big batches.
        Assert.Contains("log_records[0]", message);
        Assert.DoesNotContain("log_records[9]", message);
    }
}
