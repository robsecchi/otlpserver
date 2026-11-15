using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Compression;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace OtlpServer.Tests.Infrastructure;

/// <summary>
/// Runs the real server (same Program.cs, same DI setup) in memory and gives a gRPC client
/// connected to it.
///
/// <code>
///  test ──► LogsServiceClient ──► GrpcChannel ──► TestServer handler ──► ASP.NET Core pipeline ──► LogsGrpcService
///                                                  (in-memory HTTP/2-like transport, no sockets)
/// </code>
/// <para>
/// Each test gets its own instance, so singletons (like the admission semaphore in
/// LogsExportHandler) never leak between tests.
/// </para>
/// </summary>
public sealed class OtlpTestServer : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly GrpcChannel _channel;

    private OtlpTestServer(WebApplicationFactory<Program> factory, GrpcChannel channel, ConcurrentQueue<(LogLevel, string)> logs)
    {
        _factory = factory;
        _channel = channel;
        Logs = logs;
        Client = new LogsService.LogsServiceClient(channel);
        Http = factory.Server.CreateClient();
    }

    /// <summary>OTLP/gRPC client.</summary>
    public LogsService.LogsServiceClient Client { get; }

    /// <summary>Plain HTTP client for OTLP/HTTP (same in-memory server, same singletons).</summary>
    public HttpClient Http { get; }

    /// <summary>Everything the app logged.</summary>
    public ConcurrentQueue<(LogLevel Level, string Message)> Logs { get; }

    // Same RPC name as the generated client, but the request/response are plain bytes, so
    // tests can put anything on the wire (including bytes that aren't valid protobuf).
    private static readonly Marshaller<byte[]> RawBytes = Marshallers.Create(b => b, b => b);
    private static readonly Method<byte[], byte[]> RawExportMethod =
        new(MethodType.Unary, LogsService.Descriptor.FullName, "Export", RawBytes, RawBytes);

    /// <summary>Calls LogsService/Export with <paramref name="payload"/> as the exact message bytes.</summary>
    public Task<byte[]> ExportRawAsync(byte[] payload) =>
        _channel.CreateCallInvoker().AsyncUnaryCall(RawExportMethod, null, new CallOptions(), payload).ResponseAsync;

    /// <param name="settings">Configuration overrides, e.g. { "Otlp:Logs:MaxConcurrentExports", "1" }.</param>
    /// <param name="services">Replaces registrations (runs after Program.cs registered its own).</param>
    /// <param name="clientCompressionProviders">Compression algorithms the CLIENT can use (default: gzip only).</param>
    public static OtlpTestServer Start(
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null,
        IList<ICompressionProvider>? clientCompressionProviders = null)
    {
        var logs = new ConcurrentQueue<(LogLevel, string)>();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(logs)));

            // UseSetting goes into the app configuration before Program.cs reads it.
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                builder.UseSetting(key, value);
            }

            if (services is not null)
            {
                builder.ConfigureTestServices(services);
            }
        });

        var channelOptions = new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler(),
        };
        if (clientCompressionProviders is not null)
        {
            channelOptions.CompressionProviders = clientCompressionProviders;
        }

        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, channelOptions);
        return new OtlpTestServer(factory, channel, logs);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        _channel.Dispose();
        await _factory.DisposeAsync();
    }
}
