using Microsoft.Extensions.Options;
using OtlpServer.Logs.Failures;
using OtlpServer.Security;
using OtlpServer.Transport;

namespace OtlpServer.Admission;

/// <summary>Marks an endpoint as an OTLP export: admission applies to it.</summary>
public sealed class OtlpExportEndpointMetadata;

/// <summary>
/// Admission for OTLP exports, as request middleware. Where it sits:
/// <code>
///  request ──► routing ──► authentication / authorization ──► ADMISSION ──► endpoint: read body, decompress, decode, validate
///                                                               │                                                       │
///                                                               └──────────── slot held for all of this ────────────────┘
/// </code>
/// <para>
/// It used to be inside the core handler, which only runs AFTER the body has been read and
/// decoded. The baseline measurement showed what that meant: 128 concurrent exports against
/// a limit of 64 were never throttled, while memory grew to 1.7 GB. Here, the slot is taken
/// before a single body byte is read (gRPC included: grpc-dotnet reads the message inside the
/// endpoint), so the limit bounds memory and CPU, not just validation.
/// </para>
/// <para>
/// After authentication, so the per-client cap knows who the client is, and unauthenticated
/// requests are refused without ever taking a slot.
/// </para>
/// </summary>
public sealed class ExportAdmissionMiddleware(
    RequestDelegate next,
    ExportAdmission admission,
    IOptions<AdmissionOptions> options,
    ILogger<ExportAdmissionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<OtlpExportEndpointMetadata>() is null)
        {
            await next(http); // not an OTLP export (404, 405, health, ...)
            return;
        }

        var client = ExportContextFactory.From(http).Client;
        var decision = admission.TryEnter(client, out var lease);

        if (decision != AdmissionDecision.Admitted)
        {
            // Debug, not Warning: under overload this happens thousands of times per second, and
            // a log line each would make things worse. A metric and an aggregated warning come
            // with the observability work.
            logger.LogDebug("Refused OTLP export ({Decision}) from {Client}: {InFlight}/{Max} in flight",
                decision, client ?? "anonymous", admission.InFlight, admission.MaxConcurrentExports);

            var delay = options.Value.RetryDelay;
            await EarlyResponses.WriteAsync(http, decision == AdmissionDecision.QuotaExceeded
                ? OtlpExportException.QuotaExceeded(
                    $"Client '{client}' already has its maximum number of exports in progress. Retry after the given delay.", delay)
                : OtlpExportException.Throttled(
                    $"Server is busy ({admission.MaxConcurrentExports} exports in progress). Retry after the given delay.", delay));
            return;
        }

        using (lease)
        {
            await next(http);
        }
    }
}

public static class AdmissionExtensions
{
    public static WebApplicationBuilder AddOtlpAdmission(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AdmissionOptions>()
            .Bind(builder.Configuration.GetSection(AdmissionOptions.SectionName))
            .Validate(o => o.MaxConcurrentExports is null or > 0, $"{AdmissionOptions.SectionName}:MaxConcurrentExports must be > 0")
            .Validate(o => o.RetryDelay > TimeSpan.Zero, $"{AdmissionOptions.SectionName}:RetryDelay must be positive")
            .Validate(o => o.PerClient.MaxConcurrentExports is null or > 0,
                $"{AdmissionOptions.SectionName}:PerClient:MaxConcurrentExports must be > 0")
            .Validate(o => o.PerClient.Overrides.Values.All(v => v > 0),
                $"{AdmissionOptions.SectionName}:PerClient:Overrides values must be > 0")
            .ValidateOnStart();
        builder.Services.AddSingleton<ExportAdmission>();
        return builder;
    }

    /// <summary>After UseOtlpSecurity (needs the authenticated client), before the endpoints.</summary>
    public static WebApplication UseOtlpAdmission(this WebApplication app)
    {
        var admission = app.Services.GetRequiredService<ExportAdmission>();
        var perClient = app.Services.GetRequiredService<IOptions<AdmissionOptions>>().Value.PerClient;
        app.Logger.LogInformation(
            "Export admission: at most {Max} concurrent exports ({Source}); per-client cap: {PerClient}",
            admission.MaxConcurrentExports,
            app.Services.GetRequiredService<IOptions<AdmissionOptions>>().Value.MaxConcurrentExports is null
                ? $"default: 2 x {Environment.ProcessorCount} CPUs, min 8" : "configured",
            perClient.MaxConcurrentExports is null && perClient.Overrides.Count == 0
                ? "off"
                : $"{perClient.MaxConcurrentExports?.ToString() ?? "none"} by default, {perClient.Overrides.Count} override(s)");

        app.UseMiddleware<ExportAdmissionMiddleware>();
        return app;
    }

    /// <summary>Marks an endpoint as an OTLP export, so admission applies to it.</summary>
    public static TBuilder WithOtlpAdmission<TBuilder>(this TBuilder endpoint) where TBuilder : IEndpointConventionBuilder =>
        endpoint.WithMetadata(new OtlpExportEndpointMetadata());
}
