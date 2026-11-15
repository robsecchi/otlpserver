using OtlpServer.Logs;
using OtlpServer.Security;

namespace OtlpServer.Transport;

/// <summary>Builds the core's <see cref="ExportContext"/> from an HTTP request (both transports run on one).</summary>
public static class ExportContextFactory
{
    /// <summary>The client set by authentication (API key or JWT), if any.</summary>
    public static ExportContext From(HttpContext http) =>
        new(http.User.FindFirst(ApiKeyAuthenticationHandler.ClientNameClaim)?.Value);
}
