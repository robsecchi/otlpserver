namespace OtlpServer.Security;

/// <summary>
/// Client authentication, bound from "Otlp:Security:Authentication". Like TLS, it is OFF by
/// default (development and testing), and every endpoint without it gets a startup warning.
///
/// <para>Two methods, usable alone or together. Both arrive as <c>Authorization: Bearer &lt;credential&gt;</c>:</para>
/// <code>
///  "Mode": "ApiKey"        static keys, stored as SHA-256 hashes          (Clients)
///  "Mode": "Jwt"           tokens issued by an STS / OIDC provider        (Jwt), e.g. Keycloak
///  "Mode": "ApiKey, Jwt"   either: a credential shaped like a JWT (three dot-separated parts)
///                          is checked as a token, anything else as an API key
/// </code>
/// <para>
/// Exporters send the credential with
/// <c>OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer%20&lt;credential&gt;"</c> (space
/// URL-encoded). With JWTs, something must fetch and refresh the token: typically the OTel
/// Collector's oauth2client extension (client-credentials flow) in front of this server.
/// </para>
/// </summary>
public sealed class OtlpAuthenticationOptions
{
    public OtlpAuthenticationMode Mode { get; set; } = OtlpAuthenticationMode.None;

    /// <summary>API-key clients (Mode includes ApiKey).</summary>
    public List<ApiKeyClient> Clients { get; set; } = [];

    /// <summary>Token validation (Mode includes Jwt).</summary>
    public JwtAuthenticationOptions Jwt { get; set; } = new();
}

/// <summary>A combination of methods (flags): "ApiKey", "Jwt", or "ApiKey, Jwt" in configuration.</summary>
[Flags]
public enum OtlpAuthenticationMode
{
    /// <summary>Default, for development and testing: anyone who can reach the port can export.</summary>
    None = 0,

    /// <summary>Static bearer keys from <see cref="OtlpAuthenticationOptions.Clients"/>.</summary>
    ApiKey = 1,

    /// <summary>JWT access tokens from the STS in <see cref="OtlpAuthenticationOptions.Jwt"/>.</summary>
    Jwt = 2,
}

/// <summary>
/// API-key client. Keys are stored as SHA-256 hashes, never in clear:
/// <code>
///  printf %s "the-secret-key" | shasum -a 256        →  9f86d0...  (64 hex characters)
///
///  "Clients": [ { "Name": "billing-service", "KeyHashes": [ "9f86d0...", "2c26b4..." ] } ]   ← two keys: rotation
/// </code>
/// A leaked appsettings.json then doesn't leak usable keys. Several hashes per client let a
/// key be rotated without downtime: add the new one, move the exporters over, remove the old.
/// </summary>
public sealed class ApiKeyClient
{
    /// <summary>The client's identity once authenticated (e.g. "billing-service").</summary>
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of each valid key, as 64 hex characters.</summary>
    public List<string> KeyHashes { get; set; } = [];
}

/// <summary>
/// JWT access tokens issued by an STS / OpenID Connect provider. Example for Keycloak:
/// <code>
///  "Jwt": {
///    "Authority": "https://keycloak.example.com/realms/otel",   ← the realm's issuer URL
///    "Audiences": [ "otlp-server" ],                            ← needs an audience mapper in Keycloak
///    "RequiredScopes": [ "otlp:write" ]                         ← optional
///  }
/// </code>
/// <para>What happens per request:</para>
/// <code>
///  token ──► signature: checked with the STS's public keys, from {Authority}/.well-known/openid-configuration
///                       (downloaded on first use, cached, refreshed when the STS rotates its keys)
///        ──► iss == Authority's issuer, aud ∈ Audiences, not expired (± ClockSkew)
///        ──► scope claim contains every RequiredScope ──► otherwise 403 / PERMISSION_DENIED
/// </code>
/// </summary>
public sealed class JwtAuthenticationOptions
{
    /// <summary>
    /// The issuer's base URL. Discovery is at {Authority}/.well-known/openid-configuration.
    /// For Keycloak: https://&lt;host&gt;/realms/&lt;realm&gt;.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Accepted <c>aud</c> values. Required: without an audience check, a token the same STS
    /// issued for ANY other application would be accepted here. (Keycloak puts "account" in
    /// aud by default; add an audience mapper to the client scope for this server.)
    /// </summary>
    public List<string> Audiences { get; set; } = [];

    /// <summary>
    /// Scopes the token's <c>scope</c> claim (space-separated) must ALL contain. Empty = any valid
    /// token is enough. A valid token missing one gets 403 / PERMISSION_DENIED: authenticated,
    /// but not allowed.
    /// </summary>
    public List<string> RequiredScopes { get; set; } = [];

    /// <summary>
    /// Which claim names the client (used in logs, and later for per-client limits).
    /// Keycloak: <c>azp</c> is the client that requested the token (client-credentials flow).
    /// Falls back to <c>client_id</c>, then <c>sub</c>.
    /// </summary>
    public string ClientNameClaim { get; set; } = "azp";

    /// <summary>Tolerance for clock differences when checking exp / nbf.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Fetch discovery and keys only over https (default). Set to false ONLY for a local
    /// development STS on http://localhost: over plaintext, whoever controls the network
    /// could swap the keys and mint tokens. Warned about at startup.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// When the STS can't be reached (to get its keys), exports are answered "unavailable,
    /// retry after this delay" (gRPC UNAVAILABLE / HTTP 503), NOT 401. A 401 is non-retryable:
    /// the client would DROP the data because of an outage on our side.
    /// <para>
    /// Once the STS is back, validation recovers within about a second: after failed fetches
    /// the token library waits ~1 s before trying the STS again, so it doesn't hammer one that
    /// is down. Keep this delay above that, so a client's retry lands after recovery.
    /// </para>
    /// </summary>
    public TimeSpan UnavailableRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
