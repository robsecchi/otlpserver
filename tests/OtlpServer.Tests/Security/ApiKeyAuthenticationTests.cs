using System.Net;
using Google.Rpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OtlpServer.Logs.Validation;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.OtlpHttpClientExtensions;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Security;

/// <summary>
/// Otlp:Security:Authentication:Mode = ApiKey: every export needs "Authorization: Bearer &lt;key&gt;".
/// Spec: failures are UNAUTHENTICATED (gRPC) / 401 (HTTP), both non-retryable.
/// </summary>
public sealed class ApiKeyAuthenticationTests
{
    // ---- Valid keys ----------------------------------------------------------------------

    [Theory]
    [InlineData(ApiKeys.BillingKey)]
    [InlineData(ApiKeys.BillingOldKey)] // the client's second key: rotation without downtime
    [InlineData(ApiKeys.FrontendKey)]   // another client
    public async Task Valid_key_is_accepted_on_both_transports(string key)
    {
        await using var server = OtlpTestServer.Start(ApiKeys.Enabled());

        var grpc = await server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(key));
        using var http = await server.Http.WithKey(key).PostLogsAsync(WithRecords(ValidRecord()));

        Assert.Null(grpc.PartialSuccess);
        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
    }

    [Fact]
    public async Task Bearer_scheme_is_case_insensitive()
    {
        await using var server = OtlpTestServer.Start(ApiKeys.Enabled());
        server.Http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"bearer {ApiKeys.BillingKey}");

        using var response = await server.Http.PostLogsAsync(WithRecords(ValidRecord()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Refusals ------------------------------------------------------------------------

    [Fact]
    public async Task Missing_key_over_grpc_is_unauthenticated()
    {
        await using var server = OtlpTestServer.Start(ApiKeys.Enabled());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(WithRecords(ValidRecord())).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Contains("Missing Authorization header", ex.Status.Detail);
    }

    [Fact]
    public async Task Wrong_key_over_grpc_is_unauthenticated()
    {
        await using var server = OtlpTestServer.Start(ApiKeys.Enabled());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc("guess")).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Contains("Invalid API key", ex.Status.Detail);
    }

    [Theory]
    [InlineData(null, "Missing Authorization header")]
    [InlineData("Bearer guess", "Invalid API key")]
    [InlineData("Basic YmlsbGluZzprZXk=", "Unsupported Authorization header")]
    [InlineData("Bearer", "Unsupported Authorization header")] // scheme, no key
    public async Task Missing_or_wrong_credentials_over_http_are_401(string? authorization, string expected)
    {
        await using var server = OtlpTestServer.Start(ApiKeys.Enabled());
        if (authorization is not null)
        {
            server.Http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await server.Http.PostLogsAsync(WithRecords(ValidRecord()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme); // RFC 6750
        var status = await response.ReadStatusAsync();
        Assert.Equal((int)Code.Unauthenticated, status.Code);
        Assert.Contains(expected, status.Message);
    }

    [Fact]
    public async Task Json_request_gets_a_json_401()
    {
        await using var server = OtlpTestServer.Start(ApiKeys.Enabled());

        using var response = await server.Http.PostLogsJsonAsync("{}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal((int)Code.Unauthenticated, (await response.ReadJsonStatusAsync()).Code);
    }

    [Fact]
    public async Task Wrong_key_is_logged_without_the_key()
    {
        await using var server = OtlpTestServer.Start(ApiKeys.Enabled());

        using var _ = await server.Http.WithKey("super-secret-guess").PostLogsAsync(WithRecords(ValidRecord()));

        Assert.Contains(server.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("invalid API key"));
        Assert.DoesNotContain(server.Logs, l => l.Message.Contains("super-secret-guess"));
    }

    // ---- Authentication comes first -----------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_request_is_refused_before_the_body_is_read()
    {
        // Garbage that would be a 400, and a body that would be a 413: without a key, both are
        // 401, which shows the body was never read, decompressed or parsed.
        var settings = ApiKeys.Enabled();
        settings["Otlp:Http:MaxRequestBodySize"] = "1024";
        await using var server = OtlpTestServer.Start(settings);

        using var garbage = await server.Http.PostLogsAsync([0x0F]);
        using var tooLarge = await server.Http.PostLogsAsync(new byte[64 * 1024]);

        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tooLarge.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_does_not_take_or_need_a_concurrency_slot()
    {
        var gate = new GatedValidator();
        var settings = ApiKeys.Enabled();
        settings["Otlp:Logs:MaxConcurrentExports"] = "1";
        await using var server = OtlpTestServer.Start(settings, s => s.AddSingleton<ILogRecordValidator>(gate));

        // An authenticated export holds the only slot...
        var first = server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.BillingKey)).ResponseAsync;
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)));

        // ...an unauthenticated one is refused as UNAUTHENTICATED, not UNAVAILABLE (throttled):
        // it never got as far as asking for a slot.
        var unauthenticated = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(WithRecords(ValidRecord())).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, unauthenticated.StatusCode);

        // ...while an authenticated one does hit the limit.
        var throttled = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.FrontendKey)).ResponseAsync);
        Assert.Equal(StatusCode.Unavailable, throttled.StatusCode);

        gate.Release.Set();
        await first;
    }

    // ---- Default mode ------------------------------------------------------------------------

    [Fact]
    public async Task Default_mode_needs_no_key_and_ignores_any_sent()
    {
        await using var server = OtlpTestServer.Start();

        using var withoutKey = await server.Http.PostLogsAsync(WithRecords(ValidRecord()));
        var withJunkKey = await server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc("anything"));

        Assert.Equal(HttpStatusCode.OK, withoutKey.StatusCode);
        Assert.Null(withJunkKey.PartialSuccess);
    }

    // ---- Configuration mistakes stop the server -------------------------------------------------

    [Fact]
    public void Api_key_mode_without_clients_fails_at_startup()
    {
        var ex = Assert.ThrowsAny<Exception>(() => OtlpTestServer.Start(new Dictionary<string, string?>
        {
            ["Otlp:Security:Authentication:Mode"] = "ApiKey",
        }));

        Assert.Contains("no Clients are configured", ex.ToString());
    }

    [Fact]
    public void Key_pasted_instead_of_its_hash_fails_at_startup()
    {
        var settings = ApiKeys.Enabled();
        settings["Otlp:Security:Authentication:Clients:1:KeyHashes:0"] = ApiKeys.FrontendKey;

        var ex = Assert.ThrowsAny<Exception>(() => OtlpTestServer.Start(settings));

        Assert.Contains("Clients:1:KeyHashes:0 is not a SHA-256 hash", ex.ToString());
        Assert.DoesNotContain(ApiKeys.FrontendKey, ex.ToString()); // not even the pasted key is echoed
    }

    [Fact]
    public void Same_key_for_two_clients_fails_at_startup()
    {
        var settings = ApiKeys.Enabled();
        settings["Otlp:Security:Authentication:Clients:1:KeyHashes:0"] = ApiKeys.Hash(ApiKeys.BillingKey).ToLowerInvariant();

        var ex = Assert.ThrowsAny<Exception>(() => OtlpTestServer.Start(settings));

        Assert.Contains("is also a key of client 'billing-service'", ex.ToString());
    }

    [Theory]
    [InlineData("Otlp:Security:Authentication:Clients:1:Name", "billing-service", "is used by more than one client")]
    [InlineData("Otlp:Security:Authentication:Clients:1:Name", " ", "Name is empty")]
    // Unknown name: the configuration binder refuses it, naming the setting.
    [InlineData("Otlp:Security:Authentication:Mode", "Password", "'Otlp:Security:Authentication:Mode'")]
    // A number the binder WOULD accept as an (undefined) enum value: our own check catches it.
    [InlineData("Otlp:Security:Authentication:Mode", "7", "Mode must be None, ApiKey, Jwt")]
    public void Other_configuration_mistakes_fail_at_startup(string key, string value, string expected)
    {
        var settings = ApiKeys.Enabled();
        settings[key] = value;

        var ex = Assert.ThrowsAny<Exception>(() => OtlpTestServer.Start(settings));

        Assert.Contains(expected, ex.ToString());
    }
}
