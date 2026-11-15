using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.OtlpHttpClientExtensions;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Security;

/// <summary>
/// TLS on real sockets: both transports over https, HTTP/2 negotiated with ALPN, the startup
/// security report, and RequireSecure.
/// </summary>
public sealed class TlsTests : IClassFixture<TestCertificates>
{
    private readonly TestCertificates _tls;

    public TlsTests(TestCertificates tls) => _tls = tls;

    [Fact]
    public async Task Grpc_export_works_over_tls()
    {
        await using var server = KestrelOtlpServer.Start(_tls);

        var response = await server.GrpcClient(_tls).ExportAsync(WithRecords(ValidRecord()));

        Assert.Null(response.PartialSuccess);
    }

    [Theory]
    [InlineData("2.0")] // with TLS, 4318 can negotiate HTTP/2 (ALPN)...
    [InlineData("1.1")] // ...and still serves HTTP/1.1 exporters
    public async Task Http_export_works_over_tls_with_both_http_versions(string version)
    {
        await using var server = KestrelOtlpServer.Start(_tls);
        using var http = server.HttpClient(_tls, version: Version.Parse(version));

        using var response = await http.PostLogsAsync(WithRecords(ValidRecord()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Version.Parse(version), response.Version);
    }

    [Fact]
    public async Task Plaintext_client_cannot_use_a_tls_endpoint()
    {
        await using var server = KestrelOtlpServer.Start(_tls);
        using var http = new HttpClient { BaseAddress = new Uri($"http://{server.HttpAddress.Authority}") };

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.PostLogsAsync(WithRecords(ValidRecord())));
    }

    [Fact]
    public async Task Client_that_does_not_trust_the_server_certificate_cannot_connect()
    {
        await using var server = KestrelOtlpServer.Start(_tls);
        // Default validation: the test CA isn't in the OS trust store.
        using var http = new HttpClient { BaseAddress = server.HttpAddress };

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.PostLogsAsync(WithRecords(ValidRecord())));
    }

    // ---- Startup security report ----------------------------------------------------------------

    [Fact]
    public async Task Plaintext_endpoints_are_reported_with_a_warning_each()
    {
        await using var server = KestrelOtlpServer.Start(tls: null);

        var warnings = server.Logs.Where(l => l.Level == LogLevel.Warning && l.Message.Contains("has NO TLS")).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains(server.GrpcAddress.Authority));
        Assert.Contains(warnings, w => w.Message.Contains(server.HttpAddress.Authority));
        // Bound to 127.0.0.1 in the tests: the report says it isn't reachable from the network.
        Assert.All(warnings, w => Assert.Contains("local connections only", w.Message));
    }

    [Fact]
    public async Task Tls_endpoints_are_reported_without_tls_warnings()
    {
        await using var server = KestrelOtlpServer.Start(_tls);

        Assert.DoesNotContain(server.Logs, l => l.Message.Contains("has NO TLS"));
        Assert.Equal(2, server.Logs.Count(l => l.Level == LogLevel.Information && l.Message.Contains("TLS enabled")));
    }

    [Fact]
    public void Require_secure_refuses_to_start_with_a_plaintext_endpoint()
    {
        var ex = Assert.ThrowsAny<Exception>(() => KestrelOtlpServer.Start(tls: null,
            new Dictionary<string, string?> { ["Otlp:Security:RequireSecure"] = "true" }));

        Assert.Contains("RequireSecure is true", ex.ToString());
        Assert.Contains("has no TLS", ex.ToString());
    }

    [Fact]
    public void Require_secure_also_needs_authentication_not_just_tls()
    {
        // TLS everywhere, but anyone can still export: not "secure" yet.
        var ex = Assert.ThrowsAny<Exception>(() => KestrelOtlpServer.Start(_tls,
            new Dictionary<string, string?> { ["Otlp:Security:RequireSecure"] = "true" }));

        Assert.Contains("has no authentication", ex.ToString());
        Assert.DoesNotContain("has no TLS", ex.ToString());
    }

    [Fact]
    public void Default_mode_is_insecure_and_does_not_require_tls()
    {
        // The in-memory server used by every other test starts with no TLS settings at all.
        // Real sockets, default security settings, plaintext: starts fine (with warnings).
        var start = Record.Exception(() => KestrelOtlpServer.Start(tls: null).DisposeAsync().AsTask().GetAwaiter().GetResult());
        Assert.Null(start);
    }
}
