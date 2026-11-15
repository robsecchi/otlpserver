using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

namespace OtlpServer.Security;

/// <summary>
/// Once the server is listening, looks at the addresses it ACTUALLY bound (not at what the
/// configuration seems to say), and reports how each one is protected. Three separate
/// warnings, because the risks differ:
/// <code>
///  no TLS                      → traffic can be read and modified on the network
///  no authentication           → anyone who can reach the port can export
///  bearer keys over plaintext  → worst case: API keys / tokens can be captured and REUSED
///  JWT keys over plain http    → whoever controls the network can swap the STS keys and mint tokens
/// </code>
/// <para>
/// An endpoint counts as authenticated with API keys or JWTs, or with TLS + required client
/// certificates (mTLS: no certificate signed by the trusted CA, no connection).
/// </para>
/// <para>
/// With <see cref="SecurityOptions.RequireSecure"/>, any plaintext or unauthenticated
/// endpoint makes startup fail instead. This runs in
/// <see cref="IHostedLifecycleService.StartedAsync"/>, after Kestrel has started; throwing
/// there makes the host's start fail, so the process exits with an error instead of serving.
/// </para>
/// </summary>
public sealed class SecurityStartupReport(
    IServer server,
    IOptions<SecurityOptions> options,
    ILogger<SecurityStartupReport> logger) : IHostedLifecycleService
{
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0)
        {
            // In-memory test server: no network listener, so nothing to protect.
            return Task.CompletedTask;
        }

        var security = options.Value;
        var authentication = security.Authentication;
        // "Bearer credentials": API keys or JWTs. Either way, sent with every request.
        var bearer = authentication.Mode != OtlpAuthenticationMode.None;
        var problems = new List<string>();

        if (authentication.Mode.HasFlag(OtlpAuthenticationMode.Jwt) && !authentication.Jwt.RequireHttpsMetadata)
        {
            problems.Add("JWT signing keys are fetched without https");
            logger.LogWarning(
                "JWT validation fetches the STS's signing keys from {Authority} without requiring https " +
                "(RequireHttpsMetadata = false): whoever controls the network could swap the keys and mint " +
                "valid tokens. Only acceptable for a local development STS.",
                authentication.Jwt.Authority);
        }

        foreach (var address in addresses)
        {
            var tls = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var mtls = tls && security.ClientCertificates.Mode == ClientCertificateMode.RequireCertificate;
            var reach = DescribeReach(address);

            if (tls)
            {
                logger.LogInformation("OTLP endpoint {Address}: TLS enabled; {ClientCertificates}; {Authentication}",
                    address, DescribeClientCertificates(security.ClientCertificates), DescribeAuthentication(security));
            }
            else if (bearer)
            {
                problems.Add($"{address} has no TLS");
                logger.LogWarning(
                    "OTLP endpoint {Address} has NO TLS ({Reach}) but requires bearer credentials: the keys travel in plaintext " +
                    "and anyone on the network path can capture and REUSE them (API keys until rotated, tokens until they " +
                    "expire). Enable TLS on this endpoint before relying on them.",
                    address, reach);
            }
            else
            {
                problems.Add($"{address} has no TLS");
                logger.LogWarning(
                    "OTLP endpoint {Address} has NO TLS ({Reach}): telemetry travels unencrypted and can be " +
                    "read or modified on the network. Fine for development and testing; for anything else, " +
                    "use an https:// URL with a certificate on the Kestrel endpoint.",
                    address, reach);
            }

            if (!bearer && !mtls)
            {
                problems.Add($"{address} has no authentication");
                logger.LogWarning(
                    "OTLP endpoint {Address} has NO AUTHENTICATION ({Reach}): anyone who can reach it can export " +
                    "logs. Fine for development and testing; for anything else, set " +
                    "{Section}:Authentication:Mode to ApiKey and/or Jwt (or require client certificates over TLS).",
                    address, reach, SecurityOptions.SectionName);
            }
        }

        if (problems.Count > 0 && security.RequireSecure)
        {
            throw new InvalidOperationException(
                $"{SecurityOptions.SectionName}:{nameof(SecurityOptions.RequireSecure)} is true, but: " +
                $"{string.Join("; ", problems)}. Configure TLS and authentication, or set RequireSecure to false.");
        }

        return Task.CompletedTask;
    }

    private static string DescribeClientCertificates(ClientCertificateOptions clientCertificates) =>
        clientCertificates.Mode switch
        {
            ClientCertificateMode.NoCertificate => "no client certificates (no mTLS)",
            var mode => $"client certificates {mode}, trusted CA: " +
                        (clientCertificates.TrustedCaPath ?? "operating system trust store"),
        };

    private static string DescribeAuthentication(SecurityOptions security)
    {
        var authentication = security.Authentication;
        var methods = new List<string>();
        if (authentication.Mode.HasFlag(OtlpAuthenticationMode.ApiKey))
        {
            methods.Add($"API keys required ({authentication.Clients.Count} client(s))");
        }
        if (authentication.Mode.HasFlag(OtlpAuthenticationMode.Jwt))
        {
            var scopes = authentication.Jwt.RequiredScopes.Count == 0
                ? "no required scopes"
                : $"required scopes: {string.Join(" ", authentication.Jwt.RequiredScopes)}";
            methods.Add($"JWT from {authentication.Jwt.Authority} (audiences: {string.Join(", ", authentication.Jwt.Audiences)}; {scopes})");
        }
        return methods.Count == 0 ? "no bearer authentication" : string.Join(" or ", methods);
    }

    /// <summary>Loopback-only listeners are much less exposed than ones on every interface.</summary>
    private static string DescribeReach(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return "reachability unknown";
        }

        var isLoopback = uri.IsLoopback ||
                         (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        return isLoopback ? "local connections only" : "reachable from the network";
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
