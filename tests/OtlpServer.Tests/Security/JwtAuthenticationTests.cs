using System.Net;
using Google.Rpc;
using Grpc.Core;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.OtlpHttpClientExtensions;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Security;

/// <summary>
/// Otlp:Security:Authentication:Mode = Jwt: access tokens issued by an STS (Keycloak or any
/// OIDC provider), checked against its published keys. Uses <see cref="FakeOidcIssuer"/>.
/// </summary>
public sealed class JwtAuthenticationTests
{
    private static (OtlpTestServer Server, FakeOidcIssuer Issuer) Start(Dictionary<string, string?>? settings = null)
    {
        var issuer = new FakeOidcIssuer();
        return (OtlpTestServer.Start(settings ?? FakeOidcIssuer.Settings(), issuer.Register), issuer);
    }

    private static async Task<HttpResponseMessage> PostWithTokenAsync(OtlpTestServer server, string token)
    {
        server.Http.DefaultRequestHeaders.Remove("Authorization");
        server.Http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return await server.Http.PostLogsAsync(WithRecords(ValidRecord()));
    }

    // ---- Valid tokens ----------------------------------------------------------------------

    [Fact]
    public async Task Valid_token_is_accepted_on_both_transports()
    {
        var (server, issuer) = Start();
        await using var _ = server;
        var token = issuer.Token();

        var grpc = await server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(token));
        using var http = await PostWithTokenAsync(server, token);

        Assert.Null(grpc.PartialSuccess);
        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
    }

    [Fact]
    public async Task Signing_keys_are_downloaded_once_and_cached()
    {
        var (server, issuer) = Start();
        await using var _ = server;

        for (var i = 0; i < 3; i++)
        {
            using var response = await PostWithTokenAsync(server, issuer.Token());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(2, issuer.MetadataRequests); // discovery document + JWKS, not per request
    }

    // ---- Invalid tokens: 401 / UNAUTHENTICATED ----------------------------------------------

    [Fact]
    public async Task Missing_token_is_401_and_unauthenticated_over_grpc()
    {
        var (server, _) = Start();
        await using var __ = server;

        using var http = await server.Http.PostLogsAsync(WithRecords(ValidRecord()));
        var grpc = await Assert.ThrowsAsync<RpcException>(() => server.Client.ExportAsync(WithRecords(ValidRecord())).ResponseAsync);

        Assert.Equal(HttpStatusCode.Unauthorized, http.StatusCode);
        Assert.Contains("Missing or unsupported Authorization header", (await http.ReadStatusAsync()).Message);
        Assert.Equal(StatusCode.Unauthenticated, grpc.StatusCode);
    }

    // Tokens are built inside the test, by the issuer instance the server trusts.
    public static TheoryData<string, string> InvalidTokens() =>
        new TheoryData<string, string>
        {
            { "expired", "Token expired." },
            { "wrong-audience", "audience (aud) is not accepted" },
            { "wrong-issuer", "issuer (iss) is not accepted" },
            { "unknown-key", "signature is invalid" },
            { "no-expiry", "no expiry (exp)" },
            { "malformed", "Invalid token." },
        };

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Invalid_token_is_401_with_the_reason(string kind, string expected)
    {
        var (server, issuer) = Start();
        await using var _ = server;
        var token = kind switch
        {
            "expired" => issuer.Token(expiresIn: TimeSpan.FromMinutes(-10)), // well past the 1-minute clock skew
            "wrong-audience" => issuer.Token(audience: "some-other-app"),
            "wrong-issuer" => issuer.Token(issuer: "https://evil.test/realms/otel"),
            "unknown-key" => issuer.Token(signWithUnknownKey: true),
            "no-expiry" => issuer.TokenWithoutExpiry(),
            _ => "aaa.bbb.ccc",
        };

        using var response = await PostWithTokenAsync(server, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var status = await response.ReadStatusAsync();
        Assert.Equal((int)Code.Unauthenticated, status.Code);
        Assert.Contains(expected, status.Message);
    }

    [Fact]
    public async Task Rejected_token_is_logged_without_the_token()
    {
        var (server, issuer) = Start();
        await using var _ = server;
        var token = issuer.Token(audience: "some-other-app");

        using var __ = await PostWithTokenAsync(server, token);

        Assert.Contains(server.Logs, l => l.Message.Contains("Rejected OTLP export") && l.Message.Contains("audience"));
        Assert.DoesNotContain(server.Logs, l => l.Message.Contains(token));
    }

    // ---- Scopes: 403 / PERMISSION_DENIED ------------------------------------------------------

    [Fact]
    public async Task Valid_token_without_the_required_scope_is_403()
    {
        var (server, issuer) = Start(FakeOidcIssuer.Settings("Jwt", FakeOidcIssuer.Scope));
        await using var _ = server;
        var token = issuer.Token(scope: "profile email");

        using var http = await PostWithTokenAsync(server, token);
        var grpc = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(token)).ResponseAsync);

        // Authenticated (we know who it is), but not allowed: also non-retryable.
        Assert.Equal(HttpStatusCode.Forbidden, http.StatusCode);
        var status = await http.ReadStatusAsync();
        Assert.Equal((int)Code.PermissionDenied, status.Code);
        Assert.Contains("lacks the required scope(s): otlp:write", status.Message);
        Assert.Equal(StatusCode.PermissionDenied, grpc.StatusCode);
    }

    [Fact]
    public async Task Token_with_the_required_scope_among_others_is_accepted()
    {
        var (server, issuer) = Start(FakeOidcIssuer.Settings("Jwt", FakeOidcIssuer.Scope));
        await using var _ = server;

        using var response = await PostWithTokenAsync(server, issuer.Token(scope: "openid otlp:write profile"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- STS outage: retryable, not 401 --------------------------------------------------------

    [Fact]
    public async Task Unreachable_sts_is_503_with_retry_after_then_recovers()
    {
        var (server, issuer) = Start();
        await using var _ = server;
        var token = issuer.Token();
        issuer.Available = false;

        using var http = await PostWithTokenAsync(server, token);
        var grpc = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(token)).ResponseAsync);

        // Retryable, so the client keeps the data and comes back: the problem is ours, not its.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, http.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), http.Headers.RetryAfter?.Delta);
        Assert.Equal((int)Code.Unavailable, (await http.ReadStatusAsync()).Code);
        Assert.Equal(StatusCode.Unavailable, grpc.StatusCode);
        Assert.NotNull(grpc.GetRpcStatus()?.GetDetail<RetryInfo>());

        // The STS comes back: the same token works again, well within the Retry-After we sent.
        // (After repeated failed fetches the token library waits ~1 s before contacting the STS
        // again, so recovery isn't always on the very next request.)
        issuer.Available = true;
        var recoveredWithin = System.Diagnostics.Stopwatch.StartNew();
        HttpStatusCode last;
        do
        {
            using var retry = await PostWithTokenAsync(server, token);
            last = retry.StatusCode;
            if (last != HttpStatusCode.OK) await Task.Delay(100);
        }
        while (last != HttpStatusCode.OK && recoveredWithin.Elapsed < TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, last);
        Assert.True(recoveredWithin.Elapsed < TimeSpan.FromSeconds(5), $"recovered after {recoveredWithin.Elapsed}");
    }

    // ---- ApiKey and Jwt together ------------------------------------------------------------------

    [Fact]
    public async Task Combined_mode_accepts_either_credential()
    {
        var settings = FakeOidcIssuer.Settings("ApiKey, Jwt");
        foreach (var (key, value) in ApiKeys.Enabled().Where(kv => kv.Key.Contains(":Clients:")))
        {
            settings[key] = value;
        }
        var (server, issuer) = Start(settings);
        await using var _ = server;

        using var withKey = await PostWithTokenAsync(server, ApiKeys.BillingKey);
        using var withToken = await PostWithTokenAsync(server, issuer.Token());
        using var badKey = await PostWithTokenAsync(server, "not-a-key");
        using var badToken = await PostWithTokenAsync(server, issuer.Token(audience: "some-other-app"));

        Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);
        // Each failure is explained by the method its credential was routed to.
        Assert.Contains("Invalid API key", (await badKey.ReadStatusAsync()).Message);
        Assert.Contains("audience", (await badToken.ReadStatusAsync()).Message);
    }

    [Fact]
    public async Task Jwt_only_mode_refuses_api_keys()
    {
        var (server, _) = Start();
        await using var __ = server;

        using var response = await PostWithTokenAsync(server, ApiKeys.BillingKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Configuration mistakes stop the server ---------------------------------------------------

    [Theory]
    [InlineData("Otlp:Security:Authentication:Jwt:Authority", null, "Authority must be the token issuer's absolute URL")]
    [InlineData("Otlp:Security:Authentication:Jwt:Authority", "http://keycloak:8080/realms/otel", "Authority must use https")]
    [InlineData("Otlp:Security:Authentication:Jwt:Audiences:0", null, "Audiences must list")]
    [InlineData("Otlp:Security:Authentication:Jwt:UnavailableRetryDelay", "00:00:00", "UnavailableRetryDelay must be positive")]
    public void Jwt_configuration_mistakes_fail_at_startup(string key, string? value, string expected)
    {
        var settings = FakeOidcIssuer.Settings();
        if (value is null) settings.Remove(key); else settings[key] = value;

        var ex = Assert.ThrowsAny<Exception>(() => OtlpTestServer.Start(settings));

        Assert.Contains(expected, ex.ToString());
    }

    [Fact]
    public async Task Http_authority_is_allowed_when_https_metadata_is_explicitly_off()
    {
        // A local development Keycloak on http://localhost. (Warned about at startup; see SecurityReportTests.)
        var settings = FakeOidcIssuer.Settings();
        settings["Otlp:Security:Authentication:Jwt:Authority"] = "http://localhost:8080/realms/otel";
        settings["Otlp:Security:Authentication:Jwt:RequireHttpsMetadata"] = "false";

        await using var server = OtlpTestServer.Start(settings);
        using var response = await server.Http.PostLogsAsync(WithRecords(ValidRecord()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // starts fine; no token sent
    }
}
