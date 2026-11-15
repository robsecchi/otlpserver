using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace OtlpServer.Security;

/// <summary>
/// Configures ASP.NET Core's JwtBearer handler from <see cref="JwtAuthenticationOptions"/>, and
/// maps its outcomes onto OTLP's status codes.
///
/// <para>The STS outage problem, and why it gets special handling:</para>
/// <code>
///  token arrives ──► do we have the STS's signing keys? (discovery + JWKS, cached)
///                      ├─ yes ──► validate: signature, iss, aud, exp ──► ok / 401 (client's fault: non-retryable, correct)
///                      └─ no, STS unreachable ──► 503 / UNAVAILABLE + retry delay
///                         (NOT 401: a non-retryable answer would make the client DROP its data
///                          because of an outage on OUR side)
/// </code>
/// </summary>
public static class JwtBearerSetup
{
    public const string SchemeName = "OtlpJwt";

    private const string UnavailableItem = "otlp.jwt.unavailable";
    private const string FailureItem = "otlp.jwt.failure";

    public static void Configure(JwtBearerOptions options, JwtAuthenticationOptions jwt, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(JwtBearerSetup));

        options.Authority = jwt.Authority;
        options.RequireHttpsMetadata = jwt.RequireHttpsMetadata;
        // Keep claim names exactly as the STS wrote them ("scope", "azp", "sub"), instead of
        // .NET's legacy mapping to long URIs.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,            // the issuer comes from the discovery document
            ValidateAudience = true,
            ValidAudiences = jwt.Audiences,
            ValidateLifetime = true,
            RequireExpirationTime = true,     // a token that never expires is refused
            RequireSignedTokens = true,       // no "alg": "none"
            ValidateIssuerSigningKey = true,
            ClockSkew = jwt.ClockSkew,
        };

        options.Events = new JwtBearerEvents
        {
            // Before validation, make sure the STS's keys are available. If they can't be
            // fetched, mark the request "unavailable" so the challenge answers 503, not 401.
            OnMessageReceived = async context =>
            {
                string? header = context.Request.Headers.Authorization;
                if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return; // no token: the challenge says "missing"
                }

                try
                {
                    // Cached after the first success, so normally this costs nothing.
                    await context.Options.ConfigurationManager!.GetConfigurationAsync(context.HttpContext.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Cannot validate OTLP tokens: the STS at {Authority} is unreachable", jwt.Authority);
                    context.HttpContext.Items[UnavailableItem] = true;
                    context.Fail("The token issuer (STS) is unreachable.");
                }
            },

            OnAuthenticationFailed = context =>
            {
                var message = Describe(context.Exception);
                context.HttpContext.Items[FailureItem] = message;
                // Never log the token itself.
                logger.LogWarning("Rejected OTLP export from {RemoteIp}: {Reason}",
                    context.HttpContext.Connection.RemoteIpAddress, message);
                return Task.CompletedTask;
            },

            OnTokenValidated = context =>
            {
                // Same claims as the API-key handler, so the rest of the server doesn't care
                // which method authenticated the client.
                var principal = context.Principal!;
                var clientName = principal.FindFirst(jwt.ClientNameClaim)?.Value
                                 ?? principal.FindFirst("client_id")?.Value
                                 ?? principal.FindFirst("sub")?.Value
                                 ?? "unknown";
                ((ClaimsIdentity)principal.Identity!).AddClaims(
                [
                    new Claim(ApiKeyAuthenticationHandler.ClientNameClaim, clientName),
                    new Claim(ApiKeyAuthenticationHandler.AuthMethodClaim, "jwt"),
                ]);
                return Task.CompletedTask;
            },

            OnChallenge = async context =>
            {
                context.HandleResponse(); // replace JwtBearer's bare 401 with the OTLP-shaped answer

                if (context.HttpContext.Items.ContainsKey(UnavailableItem))
                {
                    await AuthenticationResponses.UnavailableAsync(context.HttpContext,
                        "Cannot validate the token right now: the token issuer is unreachable. Retry later.",
                        jwt.UnavailableRetryDelay);
                    return;
                }

                var message = context.HttpContext.Items[FailureItem] as string
                              ?? "Missing or unsupported Authorization header. Send 'Authorization: Bearer <token>'.";
                await AuthenticationResponses.UnauthenticatedAsync(context.HttpContext, message);
            },

            OnForbidden = context =>
            {
                var missing = MissingScopes(context.HttpContext.User, jwt.RequiredScopes);
                return AuthenticationResponses.PermissionDeniedAsync(context.HttpContext,
                    $"The token is valid but lacks the required scope(s): {string.Join(", ", missing)}.");
            },
        };
    }

    /// <summary>
    /// A short, safe reason for the client (and the log). The library's own messages can be
    /// long and, with PII logging off, mostly redacted.
    /// </summary>
    private static string Describe(Exception exception) => exception switch
    {
        SecurityTokenExpiredException => "Token expired.",
        SecurityTokenNotYetValidException => "Token not valid yet (nbf in the future; check clocks).",
        SecurityTokenNoExpirationException => "Token has no expiry (exp), which is required.",
        SecurityTokenInvalidAudienceException => "Token audience (aud) is not accepted by this server.",
        SecurityTokenInvalidIssuerException => "Token issuer (iss) is not accepted by this server.",
        SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException => "Token signature is invalid.",
        SecurityTokenMalformedException => "Token is malformed.",
        _ => "Invalid token.",
    };

    /// <summary>Required scopes not in the token's "scope" (space-separated) or "scp" (one claim per scope) claims.</summary>
    public static IReadOnlyList<string> MissingScopes(ClaimsPrincipal user, IReadOnlyList<string> required)
    {
        if (required.Count == 0) return [];

        var granted = user.FindAll("scope").Concat(user.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        return required.Where(scope => !granted.Contains(scope)).ToList();
    }

    /// <summary>JWS compact serialization: header.payload.signature, so exactly two dots.</summary>
    public static bool LooksLikeJwt(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return authorizationHeader.AsSpan(prefix.Length).Trim().Count('.') == 2;
    }

    /// <summary>Registers the JwtBearer options, bound to our settings.</summary>
    public static void AddOptions(IServiceCollection services) =>
        services.AddOptions<JwtBearerOptions>(SchemeName)
            .Configure<IOptions<SecurityOptions>, ILoggerFactory>((options, security, loggerFactory) =>
                Configure(options, security.Value.Authentication.Jwt, loggerFactory));
}
