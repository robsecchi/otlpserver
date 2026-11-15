using Grpc.AspNetCore.Server;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using OtlpServer.Admission;
using OtlpServer.Logs;
using OtlpServer.Logs.Validation;
using OtlpServer.Security;
using OtlpServer.Transport.Grpc;
using OtlpServer.Transport.Grpc.Compression;
using OtlpServer.Transport.Http;

var builder = WebApplication.CreateBuilder(args);

// Core (transport-agnostic) services.
builder.Services.Configure<LogsExportOptions>(builder.Configuration.GetSection(LogsExportOptions.SectionName));
builder.Services.AddOptions<LogValidationOptions>()
    .Bind(builder.Configuration.GetSection(LogValidationOptions.SectionName))
    // The binder accepts any number for an enum (e.g. "7"), so check the value is a real one.
    .Validate(o => Enum.IsDefined(o.DuplicateAttributeKeys),
        $"{LogValidationOptions.SectionName}:{nameof(LogValidationOptions.DuplicateAttributeKeys)} must be one of: " +
        string.Join(", ", Enum.GetNames<DuplicateAttributeKeyHandling>()))
    .ValidateOnStart();
builder.Services.AddSingleton<ILogRecordValidator, LogRecordValidator>();
builder.Services.AddSingleton<ILogsExportHandler, LogsExportHandler>();

// OTLP/gRPC transport.
builder.Services.AddOptions<GrpcTransportOptions>()
    .Bind(builder.Configuration.GetSection(GrpcTransportOptions.SectionName))
    .Validate(o => o.MaxReceiveMessageSize > 0, "Otlp:Grpc:MaxReceiveMessageSize must be > 0")
    .Validate(o => o.EffectiveMaxDecompressedMessageSize > 0, "Otlp:Grpc:MaxDecompressedMessageSize must be > 0")
    .ValidateOnStart(); // fail at startup, not on the first request

builder.Services.AddGrpc();

// Apply the transport options to grpc-dotnet. This is done through DI (instead of inside
// AddGrpc(o => ...)) so we can use the bound GrpcTransportOptions and a logger.
builder.Services.AddOptions<GrpcServiceOptions>()
    .Configure<IOptions<GrpcTransportOptions>, ILoggerFactory>((grpc, transport, loggerFactory) =>
    {
        // Limit 1 (wire size): enforced by grpc-dotnet, answers RESOURCE_EXHAUSTED.
        grpc.MaxReceiveMessageSize = transport.Value.MaxReceiveMessageSize;

        // Limit 2 (decompressed size): replaces the default providers (gzip + deflate) with
        // our bounded gzip provider only. Any other grpc-encoding → UNIMPLEMENTED.
        grpc.CompressionProviders =
        [
            new BoundedGzipCompressionProvider(
                transport.Value.EffectiveMaxDecompressedMessageSize,
                loggerFactory.CreateLogger<BoundedGzipCompressionProvider>()),
        ];

        // Responses stay uncompressed (ResponseCompressionAlgorithm left null): they are
        // empty or a short partial_success, so compressing them would only cost CPU.
    });

// OTLP/HTTP transport. The body is buffered in one array, so the limits can't exceed what
// an array can hold.
builder.Services.AddOptions<HttpTransportOptions>()
    .Bind(builder.Configuration.GetSection(HttpTransportOptions.SectionName))
    .Validate(o => o.MaxRequestBodySize is > 0 and <= int.MaxValue,
        $"{HttpTransportOptions.SectionName}:MaxRequestBodySize must be between 1 and {int.MaxValue}")
    .Validate(o => o.EffectiveMaxDecompressedBodySize is > 0 and <= int.MaxValue,
        $"{HttpTransportOptions.SectionName}:MaxDecompressedBodySize must be between 1 and {int.MaxValue}")
    .ValidateOnStart();

// Pooled buffers for OTLP/HTTP request bodies. Bodies are built from 128 KiB blocks taken
// from a pool (each block is allocated once, then reused), instead of a fresh, growing array
// per request. The pool keeps at most 128 MiB of FREE blocks for reuse; beyond that, returned
// blocks are left to the GC, so idle memory stays bounded.
builder.Services.AddSingleton(new RecyclableMemoryStreamManager(new RecyclableMemoryStreamManager.Options
{
    BlockSize = 128 * 1024,
    LargeBufferMultiple = 1024 * 1024,
    MaximumSmallPoolFreeBytes = 128 * 1024 * 1024,
    MaximumLargePoolFreeBytes = 64 * 1024 * 1024,
}));

// TLS / mTLS settings and the startup security report (warns about plaintext endpoints).
builder.AddOtlpSecurity();

// Admission (backpressure): how many exports may be in progress, server-wide and per client.
builder.AddOtlpAdmission();

var app = builder.Build();

// google.rpc.Status bodies for ASP.NET Core's own 404 / 405 on the HTTP port.
app.UseOtlpHttpErrorBodies();

// Authentication runs here: before any OTLP endpoint reads a body or takes a concurrency slot.
app.UseOtlpSecurity();

// Admission: an export takes its slot here, before its body is read, and keeps it until the
// response. Needs the authenticated client (per-client cap), so it comes after security.
app.UseOtlpAdmission();

// OTLP/gRPC: opentelemetry.proto.collector.logs.v1.LogsService/Export (port 4317).
app.MapGrpcService<LogsGrpcService>().RequireOtlpAuthentication(app).WithOtlpAdmission();

// OTLP/HTTP: POST /v1/logs (port 4318). Same ILogsExportHandler as gRPC.
// Endpoints aren't restricted to a port: each port only speaks its own protocol version
// (4317 is HTTP/2-only, which HTTP/1.1 exporters can't use; 4318 serves HTTP/1.1 in
// plaintext, and negotiates HTTP/1.1 or HTTP/2 with TLS), so in practice clients reach the right one.
app.MapOtlpHttpLogs().RequireOtlpAuthentication(app).WithOtlpAdmission();

app.Run();

// Lets the integration tests host this app in memory (WebApplicationFactory<Program>).
public partial class Program;
