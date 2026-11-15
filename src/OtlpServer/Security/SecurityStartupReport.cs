using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

namespace OtlpServer.Security;

/// <summary>
/// Once the server is listening, looks at the addresses it ACTUALLY bound (not at what the
/// configuration seems to say), and reports how each one is protected:
/// <list type="bullet">
///   <item>http://  → warning: no TLS. With <see cref="SecurityOptions.RequireSecure"/>, startup fails instead.</item>
///   <item>https:// → information: TLS on, and how client certificates are handled.</item>
/// </list>
/// <para>
/// Reading the bound addresses means the report is right however the endpoints were set
/// (appsettings, environment, --urls), and an endpoint that's plaintext while another uses TLS
/// is still caught.
/// </para>
/// <para>
/// It runs in <see cref="IHostedLifecycleService.StartedAsync"/>, after Kestrel has started.
/// Throwing there makes the host's start fail, so with RequireSecure the process exits with
/// an error instead of serving.
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

        var plaintext = new List<string>();
        foreach (var address in addresses)
        {
            if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("OTLP endpoint {Address}: TLS enabled; {ClientCertificates}",
                    address, DescribeClientCertificates(options.Value.ClientCertificates));
            }
            else
            {
                plaintext.Add(address);
                logger.LogWarning(
                    "OTLP endpoint {Address} has NO TLS ({Reach}): telemetry travels unencrypted and can be " +
                    "read or modified on the network. Fine for development and testing; for anything else, " +
                    "use an https:// URL with a certificate on the Kestrel endpoint.",
                    address, DescribeReach(address));
            }
        }

        if (plaintext.Count > 0 && options.Value.RequireSecure)
        {
            throw new InvalidOperationException(
                $"{SecurityOptions.SectionName}:{nameof(SecurityOptions.RequireSecure)} is true, but these endpoints " +
                $"have no TLS: {string.Join(", ", plaintext)}. Configure a certificate for them or set RequireSecure to false.");
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
