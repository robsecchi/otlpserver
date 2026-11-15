using Grpc.AspNetCore.Server;
using Microsoft.Extensions.Options;
using OtlpServer.Logs;
using OtlpServer.Logs.Validation;
using OtlpServer.Transport.Grpc;
using OtlpServer.Transport.Grpc.Compression;

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

var app = builder.Build();

app.MapGrpcService<LogsGrpcService>();

// OTLP/HTTP transport (POST /v1/logs on port 4318) will be mapped here later,
// reusing ILogsExportHandler.

app.Run();

// Lets the integration tests host this app in memory (WebApplicationFactory<Program>).
public partial class Program;
