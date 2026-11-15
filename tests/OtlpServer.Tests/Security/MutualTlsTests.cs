using System.Net;
using Microsoft.Extensions.Logging;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.OtlpHttpClientExtensions;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Security;

/// <summary>
/// Mutual TLS with a custom CA (Otlp:Security:ClientCertificates): only clients whose
/// certificate is signed by OUR CA, for client authentication, can connect.
/// </summary>
public sealed class MutualTlsTests : IClassFixture<TestCertificates>
{
    private readonly TestCertificates _tls;

    public MutualTlsTests(TestCertificates tls) => _tls = tls;

    private Dictionary<string, string?> RequireClientCertificates(string mode = "RequireCertificate") => new()
    {
        ["Otlp:Security:ClientCertificates:Mode"] = mode,
        ["Otlp:Security:ClientCertificates:TrustedCaPath"] = _tls.CaPath,
    };

    [Fact]
    public async Task Client_certificate_signed_by_the_trusted_ca_is_accepted()
    {
        await using var server = KestrelOtlpServer.Start(_tls, RequireClientCertificates());

        using var http = server.HttpClient(_tls, _tls.ClientCertificate);
        using var response = await http.PostLogsAsync(WithRecords(ValidRecord()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // And over gRPC too.
        var grpc = await server.GrpcClient(_tls, _tls.ClientCertificate).ExportAsync(WithRecords(ValidRecord()));
        Assert.Null(grpc.PartialSuccess);
    }

    [Fact]
    public async Task No_client_certificate_is_refused()
    {
        await using var server = KestrelOtlpServer.Start(_tls, RequireClientCertificates());
        using var http = server.HttpClient(_tls);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.PostLogsAsync(WithRecords(ValidRecord())));
    }

    [Fact]
    public async Task Client_certificate_from_another_ca_is_refused_and_logged()
    {
        await using var server = KestrelOtlpServer.Start(_tls, RequireClientCertificates());
        using var http = server.HttpClient(_tls, _tls.UntrustedClientCertificate);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.PostLogsAsync(WithRecords(ValidRecord())));
        Assert.Contains(server.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("Rejected client certificate 'CN=intruder'"));
    }

    [Fact]
    public async Task Certificate_from_the_trusted_ca_but_not_for_client_auth_is_refused()
    {
        // Same CA, but issued for SERVER authentication: it must not work as a client identity.
        await using var server = KestrelOtlpServer.Start(_tls, RequireClientCertificates());
        using var http = server.HttpClient(_tls, _tls.ServerUsedAsClient);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.PostLogsAsync(WithRecords(ValidRecord())));
        // Refused by OUR usage check (not, say, because the client never sent it).
        Assert.Contains(server.Logs, l => l.Message.Contains("Rejected client certificate 'CN=not-a-client'"));
    }

    [Fact]
    public async Task Allow_mode_accepts_clients_without_a_certificate()
    {
        await using var server = KestrelOtlpServer.Start(_tls, RequireClientCertificates("AllowCertificate"));
        using var http = server.HttpClient(_tls);

        using var response = await http.PostLogsAsync(WithRecords(ValidRecord()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Startup_report_describes_the_client_certificate_policy()
    {
        await using var server = KestrelOtlpServer.Start(_tls, RequireClientCertificates());

        Assert.Contains(server.Logs, l => l.Message.Contains("client certificates RequireCertificate") && l.Message.Contains(_tls.CaPath));
    }

    // ---- Misconfiguration fails at startup ----------------------------------------------------

    [Fact]
    public void Trusted_ca_without_client_certificates_enabled_fails_at_startup()
    {
        var ex = Assert.ThrowsAny<Exception>(() => KestrelOtlpServer.Start(_tls, new Dictionary<string, string?>
        {
            ["Otlp:Security:ClientCertificates:TrustedCaPath"] = _tls.CaPath, // Mode left at NoCertificate
        }));

        Assert.Contains("TrustedCaPath is set, but Mode is NoCertificate", ex.ToString());
    }

    [Fact]
    public void Missing_ca_file_fails_at_startup()
    {
        var settings = RequireClientCertificates();
        settings["Otlp:Security:ClientCertificates:TrustedCaPath"] = "/does/not/exist/ca.crt";

        Assert.ThrowsAny<Exception>(() => KestrelOtlpServer.Start(_tls, settings));
    }

    [Fact]
    public void Ca_file_without_certificates_fails_at_startup()
    {
        var settings = RequireClientCertificates();
        settings["Otlp:Security:ClientCertificates:TrustedCaPath"] = _tls.EmptyPemPath;

        var ex = Assert.ThrowsAny<Exception>(() => KestrelOtlpServer.Start(_tls, settings));
        Assert.Contains("No certificate found", ex.ToString());
    }
}
