using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OtlpServer.Diagnostics;

namespace OtlpServer.Security;

/// <summary>
/// ASP.NET Core authentication handler for <c>Authorization: Bearer &lt;key&gt;</c>.
///
/// <para>Where it runs in a request:</para>
/// <code>
///  request ──► routing ──► authentication (this handler) ──► authorization ──┬─ allowed ──► endpoint: read body, take a slot, export
///                                                                            └─ refused ──► 401, the body is never read
/// </code>
/// <para>
/// So a client without a valid key can't make the server read, decompress or parse anything,
/// and can't take one of the concurrency slots.
/// </para>
/// <para>How the key is checked:</para>
/// <list type="number">
///   <item>Hash the presented key with SHA-256.</item>
///   <item>Compare that hash with EVERY configured hash, in constant time
///         (<see cref="CryptographicOperations.FixedTimeEquals"/>), with no early exit. So the
///         response time says nothing about how close a guess was, or which client matched.</item>
/// </list>
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiKeyStore keys,
    AggregatedWarnings warnings,
    IOptions<SecurityOptions> security)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "OtlpApiKey";

    /// <summary>Claim carrying the authenticated client's name (both methods set it).</summary>
    public const string ClientNameClaim = "otlp_client";

    /// <summary>Claim saying which method authenticated the client: "apikey" or "jwt".</summary>
    public const string AuthMethodClaim = "otlp_auth_method";

    private const string MissingHeader = "Missing Authorization header. Send 'Authorization: Bearer <key>'.";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // ASP.NET Core authenticates EVERY request with the default scheme, whatever the mode. With
        // authentication off, there is nothing to check: don't hash keys or log anything.
        if (security.Value.Authentication.Mode == OtlpAuthenticationMode.None)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // No credentials is "no result", not a failure (as ASP.NET's own handlers do): the
        // challenge still answers 401 "missing", and nothing is logged per request.
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!AuthenticationHeaderValue.TryParse(header, out var value) ||
            !value.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(value.Parameter))
        {
            return Task.FromResult(AuthenticateResult.Fail("Unsupported Authorization header. Send 'Authorization: Bearer <key>'."));
        }

        if (keys.FindClient(value.Parameter) is not { } clientName)
        {
            // Never log the key itself, not even a prefix. Aggregated per remote address: under
            // attack, a line per bad key would flood the log.
            warnings.Report("Rejected OTLP export: invalid API key",
                Context.Connection.RemoteIpAddress?.ToString() ?? "unknown address",
                "the presented API key matches no configured client");
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, clientName),
                new Claim(ClientNameClaim, clientName),
                new Claim(AuthMethodClaim, "apikey"),
            ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>
    /// Called when the endpoint requires authentication and it failed or was missing: answers
    /// UNAUTHENTICATED / 401 in the OTLP shape (see <see cref="AuthenticationResponses"/>).
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync();
        await AuthenticationResponses.UnauthenticatedAsync(Context, result.Failure?.Message ?? MissingHeader);
    }

    /// <summary>Authenticated, but the authorization policy said no: PERMISSION_DENIED / 403.</summary>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        AuthenticationResponses.PermissionDeniedAsync(Context, "This client is not allowed to export.");
}

/// <summary>
/// The configured key hashes, decoded once at startup. Lookup is linear and constant-time on
/// purpose: there are a handful of clients, and it keeps timing from leaking anything.
/// </summary>
public sealed class ApiKeyStore
{
    private readonly (byte[] Hash, string ClientName)[] _entries;

    public ApiKeyStore(IOptions<SecurityOptions> options)
    {
        _entries = options.Value.Authentication.Clients
            .SelectMany(client => client.KeyHashes.Select(hash => (Convert.FromHexString(hash), client.Name)))
            .ToArray();
    }

    /// <returns>The client's name, or null if the key matches none.</returns>
    public string? FindClient(string presentedKey)
    {
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));

        string? match = null;
        foreach (var (hash, clientName) in _entries)
        {
            // No early exit: every entry is compared, whatever happened before.
            if (CryptographicOperations.FixedTimeEquals(presentedHash, hash))
            {
                match = clientName;
            }
        }
        return match;
    }
}
