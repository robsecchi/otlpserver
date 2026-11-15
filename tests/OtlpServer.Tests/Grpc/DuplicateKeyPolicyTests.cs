using Grpc.Core;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Grpc;

/// <summary>
/// End to end: how a duplicate key on a resource or scope affects the records under it,
/// with the default policy and with Otlp:Logs:Validation:DuplicateAttributeKeys=AcceptWithWarning.
/// </summary>
public sealed class DuplicateKeyPolicyTests
{
    private static readonly Dictionary<string, string?> AcceptDirtyData = new()
    {
        ["Otlp:Logs:Validation:DuplicateAttributeKeys"] = "AcceptWithWarning",
    };

    [Fact]
    public async Task Bad_resource_rejects_all_its_records_but_not_other_resources()
    {
        await using var server = OtlpTestServer.Start();

        var request = WithResources(
            ResourceLogsOf(ResourceWithDuplicateKey(),
                ScopeLogsOf(DefaultScope(), ValidRecord(), ValidRecord()),
                ScopeLogsOf(DefaultScope(), ValidRecord())),
            ResourceLogsOf(DefaultResource(),
                ScopeLogsOf(DefaultScope(), ValidRecord())));

        var response = await server.Client.ExportAsync(request);

        // 3 records under the bad resource are rejected; the 1 under the good one is kept.
        Assert.Equal(3, response.PartialSuccess.RejectedLogRecords);
        Assert.Contains("3 of 4 log record(s) rejected", response.PartialSuccess.ErrorMessage);
        Assert.Contains("duplicate_resource_attribute_key", response.PartialSuccess.ErrorMessage);
        Assert.Contains("resource_logs[0].resource", response.PartialSuccess.ErrorMessage);
    }

    [Fact]
    public async Task Bad_scope_rejects_only_its_records()
    {
        await using var server = OtlpTestServer.Start();

        var request = WithResources(ResourceLogsOf(DefaultResource(),
            ScopeLogsOf(ScopeWithDuplicateKey(), ValidRecord(), ValidRecord()),
            ScopeLogsOf(DefaultScope(), ValidRecord())));

        var response = await server.Client.ExportAsync(request);

        Assert.Equal(2, response.PartialSuccess.RejectedLogRecords);
        Assert.Contains("resource_logs[0].scope_logs[0].scope", response.PartialSuccess.ErrorMessage);
    }

    [Fact]
    public async Task Only_a_bad_resource_in_the_request_is_invalid_argument()
    {
        await using var server = OtlpTestServer.Start();

        var request = WithResources(ResourceLogsOf(ResourceWithDuplicateKey(),
            ScopeLogsOf(DefaultScope(), ValidRecord())));

        var ex = await Assert.ThrowsAsync<RpcException>(() => server.Client.ExportAsync(request).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Bad_resource_without_records_is_ignored()
    {
        // Nothing to drop, so nothing to report.
        await using var server = OtlpTestServer.Start();

        var response = await server.Client.ExportAsync(WithResources(
            ResourceLogsOf(ResourceWithDuplicateKey(), ScopeLogsOf(DefaultScope())),
            ResourceLogsOf(DefaultResource(), ScopeLogsOf(DefaultScope(), ValidRecord()))));

        Assert.Null(response.PartialSuccess);
    }

    [Fact]
    public async Task Accept_policy_keeps_dirty_data_and_reports_warnings()
    {
        await using var server = OtlpTestServer.Start(AcceptDirtyData);

        var request = WithResources(ResourceLogsOf(ResourceWithDuplicateKey(),
            ScopeLogsOf(ScopeWithDuplicateKey(), ValidRecord(), RecordWithDuplicateKey())));

        var response = await server.Client.ExportAsync(request);

        // Everything accepted, but the sender is still told what's wrong.
        Assert.Equal(0, response.PartialSuccess.RejectedLogRecords);
        var message = response.PartialSuccess.ErrorMessage;
        Assert.Contains("0 of 2 log record(s) rejected; 3 warning(s)", message);
        Assert.Contains("[warning] duplicate_resource_attribute_key", message);
        Assert.Contains("[warning] duplicate_scope_attribute_key", message);
        Assert.Contains("[warning] duplicate_attribute_key", message);
    }

    [Theory]
    [InlineData("Quarantine")] // not a defined value
    [InlineData("7")]          // a number the binder would happily turn into an undefined enum
    public void Invalid_policy_value_fails_at_startup(string value)
    {
        var ex = Assert.ThrowsAny<Exception>(() => OtlpTestServer.Start(new Dictionary<string, string?>
        {
            ["Otlp:Logs:Validation:DuplicateAttributeKeys"] = value,
        }));

        Assert.Contains("DuplicateAttributeKeys", ex.ToString());
    }
}
