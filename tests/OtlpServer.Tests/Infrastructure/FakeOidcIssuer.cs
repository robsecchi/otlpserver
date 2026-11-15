using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OtlpServer.Security;

namespace OtlpServer.Tests.Infrastructure;

/// <summary>
/// An in-memory stand-in for an STS such as Keycloak. It serves what a real one serves for
/// token VALIDATION (the discovery document and the public signing keys, JWKS) and it signs
/// tokens like a realm would. The server's JwtBearer handler talks to it through its HTTP
/// "backchannel", so the real discovery + key download path is what gets tested.
/// </summary>
public sealed class FakeOidcIssuer
{
    public const string Authority = "https://sts.test/realms/otel";
    public const string Audience = "otlp-server";
    public const string Scope = "otlp:write";

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "realm-key-1" };
    private readonly RsaSecurityKey _unknownKey = new(RSA.Create(2048)) { KeyId = "not-published" };
    private int _metadataRequests;

    /// <summary>Set to false to simulate the STS being down.</summary>
    public bool Available { get; set; } = true;

    /// <summary>How many times the server fetched discovery or keys (they should be cached).</summary>
    public int MetadataRequests => _metadataRequests;

    /// <summary>Settings enabling JWT authentication against this issuer.</summary>
    public static Dictionary<string, string?> Settings(string mode = "Jwt", params string[] requiredScopes)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Otlp:Security:Authentication:Mode"] = mode,
            ["Otlp:Security:Authentication:Jwt:Authority"] = Authority,
            ["Otlp:Security:Authentication:Jwt:Audiences:0"] = Audience,
        };
        for (var i = 0; i < requiredScopes.Length; i++)
        {
            settings[$"Otlp:Security:Authentication:Jwt:RequiredScopes:{i}"] = requiredScopes[i];
        }
        return settings;
    }

    /// <summary>Points the server's JwtBearer at this fake instead of the network.</summary>
    public void Register(IServiceCollection services) =>
        services.Configure<JwtBearerOptions>(JwtBearerSetup.SchemeName, o => o.BackchannelHttpHandler = new Backchannel(this));

    /// <summary>A token like Keycloak's client-credentials flow issues (azp = the client).</summary>
    public string Token(
        string audience = Audience,
        string issuer = Authority,
        string scope = "profile " + Scope,
        string client = "billing-service",
        TimeSpan? expiresIn = null,
        bool signWithUnknownKey = false)
    {
        var now = DateTime.UtcNow;
        var expires = now + (expiresIn ?? TimeSpan.FromMinutes(5));
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = expires < now ? expires.AddMinutes(-10) : now,
            NotBefore = expires < now ? expires.AddMinutes(-10) : now,
            Expires = expires,
            Claims = new Dictionary<string, object> { ["azp"] = client, ["scope"] = scope },
            SigningCredentials = new SigningCredentials(signWithUnknownKey ? _unknownKey : _signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>Signed correctly but with no exp claim at all.</summary>
    public string TokenWithoutExpiry()
    {
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = Audience,
            Claims = new Dictionary<string, object> { ["azp"] = "billing-service" },
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private sealed class Backchannel(FakeOidcIssuer issuer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!issuer.Available)
            {
                throw new HttpRequestException("Connection refused (simulated STS outage)");
            }
            Interlocked.Increment(ref issuer._metadataRequests);

            var path = request.RequestUri!.AbsoluteUri;
            object? body = path switch
            {
                $"{Authority}/.well-known/openid-configuration" => new
                {
                    issuer = Authority,
                    jwks_uri = $"{Authority}/protocol/openid-connect/certs",
                },
                // PUBLIC key only (n, e): exactly what a real STS publishes.
                $"{Authority}/protocol/openid-connect/certs" => new
                {
                    keys = new[]
                    {
                        new
                        {
                            kty = "RSA", use = "sig", alg = "RS256", kid = issuer._signingKey.KeyId,
                            n = Base64UrlEncoder.Encode(issuer._signingKey.Rsa.ExportParameters(false).Modulus),
                            e = Base64UrlEncoder.Encode(issuer._signingKey.Rsa.ExportParameters(false).Exponent),
                        },
                    },
                },
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }
    }
}
