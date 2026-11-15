using Microsoft.Extensions.Logging;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Security;

/// <summary>
/// The startup security report, on real sockets: one warning per risk and per endpoint, and
/// RequireSecure needing BOTH TLS and authentication.
/// </summary>
public sealed class SecurityReportTests : IClassFixture<TestCertificates>
{
    private readonly TestCertificates _tls;

    public SecurityReportTests(TestCertificates tls) => _tls = tls;

    private static List<string> Warnings(KestrelOtlpServer server, string containing) =>
        server.Logs.Where(l => l.Level == LogLevel.Warning && l.Message.Contains(containing)).Select(l => l.Message).ToList();

    [Fact]
    public async Task Default_mode_warns_about_missing_tls_and_authentication_on_each_endpoint()
    {
        await using var server = KestrelOtlpServer.Start(tls: null);

        Assert.Equal(2, Warnings(server, "has NO TLS").Count);
        Assert.Equal(2, Warnings(server, "has NO AUTHENTICATION").Count);
    }

    [Fact]
    public async Task Api_keys_over_plaintext_get_the_stronger_warning()
    {
        await using var server = KestrelOtlpServer.Start(tls: null, ApiKeys.Enabled());

        var warnings = Warnings(server, "the keys travel in plaintext");
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("REUSE", w));
        Assert.Empty(Warnings(server, "has NO AUTHENTICATION")); // they ARE authenticated
    }

    [Fact]
    public async Task Tls_plus_api_keys_has_no_warnings()
    {
        await using var server = KestrelOtlpServer.Start(_tls, ApiKeys.Enabled());

        Assert.DoesNotContain(server.Logs, l => l.Level >= LogLevel.Warning);
        Assert.Equal(2, server.Logs.Count(l => l.Message.Contains("API keys required (2 client(s))")));
    }

    [Fact]
    public async Task Tls_plus_required_client_certificates_counts_as_authenticated()
    {
        await using var server = KestrelOtlpServer.Start(_tls, new Dictionary<string, string?>
        {
            ["Otlp:Security:ClientCertificates:Mode"] = "RequireCertificate",
            ["Otlp:Security:ClientCertificates:TrustedCaPath"] = _tls.CaPath,
        });

        Assert.Empty(Warnings(server, "has NO AUTHENTICATION"));
    }

    // ---- RequireSecure -------------------------------------------------------------------------

    [Fact]
    public async Task Require_secure_starts_with_tls_and_api_keys()
    {
        var settings = ApiKeys.Enabled();
        settings["Otlp:Security:RequireSecure"] = "true";
        await using var server = KestrelOtlpServer.Start(_tls, settings);

        var response = await server.GrpcClient(_tls).ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.BillingKey));
        Assert.Null(response.PartialSuccess);
    }

    [Fact]
    public async Task Require_secure_starts_with_tls_and_mtls()
    {
        await using var server = KestrelOtlpServer.Start(_tls, new Dictionary<string, string?>
        {
            ["Otlp:Security:RequireSecure"] = "true",
            ["Otlp:Security:ClientCertificates:Mode"] = "RequireCertificate",
            ["Otlp:Security:ClientCertificates:TrustedCaPath"] = _tls.CaPath,
        });

        var response = await server.GrpcClient(_tls, _tls.ClientCertificate).ExportAsync(WithRecords(ValidRecord()));
        Assert.Null(response.PartialSuccess);
    }

    // ---- JWT ----------------------------------------------------------------------------------

    [Fact]
    public async Task Tls_plus_jwt_is_reported_without_warnings()
    {
        await using var server = KestrelOtlpServer.Start(_tls, FakeOidcIssuer.Settings("Jwt", FakeOidcIssuer.Scope));

        Assert.DoesNotContain(server.Logs, l => l.Level >= LogLevel.Warning);
        Assert.Equal(2, server.Logs.Count(l =>
            l.Message.Contains($"JWT from {FakeOidcIssuer.Authority}") && l.Message.Contains("required scopes: otlp:write")));
    }

    [Fact]
    public async Task Jwt_keys_over_plain_http_get_a_warning()
    {
        var settings = FakeOidcIssuer.Settings();
        settings["Otlp:Security:Authentication:Jwt:Authority"] = "http://localhost:8080/realms/otel";
        settings["Otlp:Security:Authentication:Jwt:RequireHttpsMetadata"] = "false";

        await using var server = KestrelOtlpServer.Start(_tls, settings);

        var warning = Assert.Single(Warnings(server, "without requiring https"));
        Assert.Contains("mint valid tokens", warning);
    }

    [Fact]
    public async Task Require_secure_starts_with_tls_and_jwt()
    {
        var settings = FakeOidcIssuer.Settings();
        settings["Otlp:Security:RequireSecure"] = "true";

        await using var server = KestrelOtlpServer.Start(_tls, settings);

        Assert.Empty(Warnings(server, "NO"));
    }

    [Fact]
    public void Require_secure_refuses_jwt_keys_over_plain_http()
    {
        var settings = FakeOidcIssuer.Settings();
        settings["Otlp:Security:RequireSecure"] = "true";
        settings["Otlp:Security:Authentication:Jwt:Authority"] = "http://localhost:8080/realms/otel";
        settings["Otlp:Security:Authentication:Jwt:RequireHttpsMetadata"] = "false";

        var ex = Assert.ThrowsAny<Exception>(() => KestrelOtlpServer.Start(_tls, settings));

        Assert.Contains("JWT signing keys are fetched without https", ex.ToString());
    }

    [Fact]
    public void Require_secure_refuses_api_keys_over_plaintext()
    {
        var settings = ApiKeys.Enabled();
        settings["Otlp:Security:RequireSecure"] = "true";

        var ex = Assert.ThrowsAny<Exception>(() => KestrelOtlpServer.Start(tls: null, settings));

        Assert.Contains("has no TLS", ex.ToString());
        Assert.DoesNotContain("has no authentication", ex.ToString());
    }
}
