using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace OtlpServer.Tests.Infrastructure;

/// <summary>
/// Runs the real app on REAL Kestrel sockets (unlike <see cref="OtlpTestServer"/>, which is
/// in memory). Needed for anything TLS: the handshake, certificates and ALPN only exist on a
/// real connection.
/// <para>
/// Both OTLP endpoints get free loopback ports, so tests never clash with a server on
/// 4317 / 4318 or with each other. Everything the app logs is captured in <see cref="Logs"/>.
/// </para>
/// </summary>
public sealed class KestrelOtlpServer : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    private KestrelOtlpServer(WebApplicationFactory<Program> factory, Uri grpc, Uri http, ConcurrentQueue<(LogLevel, string)> logs)
    {
        _factory = factory;
        GrpcAddress = grpc;
        HttpAddress = http;
        Logs = logs;
    }

    public Uri GrpcAddress { get; }
    public Uri HttpAddress { get; }
    public ConcurrentQueue<(LogLevel Level, string Message)> Logs { get; }

    /// <param name="tls">null = plaintext; otherwise both endpoints use https with this server certificate.</param>
    /// <param name="settings">Extra configuration (e.g. Otlp:Security:*).</param>
    public static KestrelOtlpServer Start(TestCertificates? tls, IDictionary<string, string?>? settings = null)
    {
        var scheme = tls is null ? "http" : "https";
        var grpc = new Uri($"{scheme}://127.0.0.1:{FreePort()}");
        var http = new Uri($"{scheme}://127.0.0.1:{FreePort()}");
        var logs = new ConcurrentQueue<(LogLevel, string)>();

        var config = new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:OtlpGrpc:Url"] = grpc.ToString(),
            ["Kestrel:Endpoints:OtlpHttp:Url"] = http.ToString(),
        };
        if (tls is not null)
        {
            foreach (var endpoint in (string[])["OtlpGrpc", "OtlpHttp"])
            {
                config[$"Kestrel:Endpoints:{endpoint}:Certificate:Path"] = tls.ServerCertPath;
                config[$"Kestrel:Endpoints:{endpoint}:Certificate:KeyPath"] = tls.ServerKeyPath;
            }
        }
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            config[key] = value;
        }

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in config) builder.UseSetting(key, value);
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(logs)));
        });
        factory.UseKestrel();
        factory.StartServer(); // throws if startup fails (e.g. RequireSecure, bad CA file)

        return new KestrelOtlpServer(factory, grpc, http, logs);
    }

    /// <summary>
    /// An HTTP client that trusts exactly this test's server certificate (the test CA isn't in
    /// the OS trust store) and optionally presents a client certificate (mTLS).
    /// </summary>
    public static SocketsHttpHandler Handler(TestCertificates? tls, X509Certificate2? clientCertificate = null) => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                tls is not null && certificate?.GetCertHashString() == tls.ServerCertificate.GetCertHashString(),
            ClientCertificates = clientCertificate is null ? null : new X509CertificateCollection { clientCertificate },
        },
    };

    public HttpClient HttpClient(TestCertificates? tls, X509Certificate2? clientCertificate = null, Version? version = null) =>
        new(Handler(tls, clientCertificate))
        {
            BaseAddress = HttpAddress,
            DefaultRequestVersion = version ?? HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

    public LogsService.LogsServiceClient GrpcClient(TestCertificates? tls, X509Certificate2? clientCertificate = null) =>
        new(GrpcChannel.ForAddress(GrpcAddress, new GrpcChannelOptions { HttpHandler = Handler(tls, clientCertificate) }));

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<(LogLevel, string)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(logs);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<(LogLevel, string)> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                logs.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
