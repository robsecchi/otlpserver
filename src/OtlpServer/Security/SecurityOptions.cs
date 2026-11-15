using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace OtlpServer.Security;

/// <summary>
/// Security settings, bound from the "Otlp:Security" configuration section.
///
/// <para>
/// The DEFAULT is insecure on purpose, for development and testing: plaintext, no client
/// certificates, no authentication. A warning is logged at startup for each endpoint
/// running that way (see <see cref="SecurityStartupReport"/>).
/// </para>
/// <para>Turning TLS on is plain Kestrel configuration: an https:// URL plus a certificate:</para>
/// <code>
///  "Kestrel": { "Endpoints": {
///    "OtlpGrpc": { "Url": "https://0.0.0.0:4317", "Protocols": "Http2",
///                  "Certificate": { "Path": "server.crt", "KeyPath": "server.key" } },
///    "OtlpHttp": { "Url": "https://0.0.0.0:4318", "Protocols": "Http1AndHttp2",
///                  "Certificate": { "Path": "server.crt", "KeyPath": "server.key" } } } }
/// </code>
/// <para>
/// Exporters then use an https:// endpoint and trust the server certificate with
/// <c>OTEL_EXPORTER_OTLP_CERTIFICATE</c>. With TLS, 4318 also offers HTTP/2: the client and
/// server agree on HTTP/1.1 or HTTP/2 during the handshake (ALPN).
/// </para>
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Otlp:Security";

    /// <summary>
    /// Opt-in hardening. When true, the server REFUSES TO START if any endpoint is plaintext,
    /// instead of only logging a warning. Meant for deployments: one setting guarantees the
    /// server never runs open by accident.
    /// </summary>
    public bool RequireSecure { get; set; }

    /// <summary>Mutual TLS: whether clients must present a certificate, and which CA must have signed it.</summary>
    public ClientCertificateOptions ClientCertificates { get; set; } = new();
}

/// <summary>
/// Mutual TLS (mTLS): the client proves who it is with its own certificate, set on exporters
/// with <c>OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE</c> / <c>OTEL_EXPORTER_OTLP_CLIENT_KEY</c>.
/// Applies to every https:// endpoint (an endpoint's own <c>ClientCertificateMode</c> in the
/// Kestrel section still wins).
/// </summary>
public sealed class ClientCertificateOptions
{
    /// <summary>
    /// <list type="bullet">
    ///   <item>NoCertificate (default): no mTLS.</item>
    ///   <item>AllowCertificate: a certificate is optional, but one that is sent must be valid.</item>
    ///   <item>RequireCertificate: no valid certificate, no connection.</item>
    /// </list>
    /// </summary>
    public ClientCertificateMode Mode { get; set; } = ClientCertificateMode.NoCertificate;

    /// <summary>
    /// PEM file with the CA certificate(s) allowed to sign client certificates, typically your
    /// own internal CA. When unset, client certificates are checked against the operating
    /// system's trust store, which is rarely what you want for mTLS: it would accept any
    /// certificate from any public CA.
    /// </summary>
    public string? TrustedCaPath { get; set; }
}
