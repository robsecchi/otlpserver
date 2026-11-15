using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

namespace OtlpServer.Security;

public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the security settings, applies the client-certificate (mTLS) settings to every
    /// HTTPS endpoint, and adds the startup report.
    /// </summary>
    public static WebApplicationBuilder AddOtlpSecurity(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<SecurityOptions>()
            .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
            .Validate(o => Enum.IsDefined(o.ClientCertificates.Mode),
                $"{SecurityOptions.SectionName}:ClientCertificates:Mode must be one of: " +
                string.Join(", ", Enum.GetNames<ClientCertificateMode>()))
            // A CA with mTLS off would never be used. Better to fail than to look protected.
            .Validate(o => o.ClientCertificates.TrustedCaPath is null || o.ClientCertificates.Mode != ClientCertificateMode.NoCertificate,
                $"{SecurityOptions.SectionName}:ClientCertificates:TrustedCaPath is set, but Mode is NoCertificate, " +
                "so it would never be used. Set Mode to RequireCertificate (or AllowCertificate).")
            .ValidateOnStart();

        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            var clientCertificates = context.Configuration.GetSection(SecurityOptions.SectionName)
                .Get<SecurityOptions>()?.ClientCertificates ?? new ClientCertificateOptions();

            // HTTPS defaults apply to every https:// endpoint, including those defined in the
            // Kestrel configuration section (unless an endpoint sets its own mode).
            kestrel.ConfigureHttpsDefaults(https =>
            {
                https.ClientCertificateMode = clientCertificates.Mode;

                if (clientCertificates.TrustedCaPath is { } caPath && clientCertificates.Mode != ClientCertificateMode.NoCertificate)
                {
                    // Loaded now, so a missing or empty CA file stops the server at startup.
                    var roots = ClientCertificateValidator.LoadTrustedRoots(caPath);
                    ClientCertificateValidator? validator = null;
                    https.ClientCertificateValidation = (certificate, chain, errors) =>
                    {
                        // Created on first use: the logger comes from DI, which isn't ready yet here.
                        validator ??= new ClientCertificateValidator(roots,
                            kestrel.ApplicationServices.GetRequiredService<ILogger<ClientCertificateValidator>>());
                        return validator.Validate(certificate, chain, errors);
                    };
                }
            });
        });

        // Authentication. All schemes are always registered; endpoints only REQUIRE them when
        // Mode isn't None (see RequireOtlpAuthentication).
        //
        //  "OtlpBearer" (policy scheme, the default) ──► picks the real scheme per request:
        //      Mode ApiKey       → OtlpApiKey
        //      Mode Jwt          → OtlpJwt
        //      Mode ApiKey, Jwt  → OtlpJwt if the credential looks like a JWT, else OtlpApiKey
        builder.Services.AddSingleton<IValidateOptions<SecurityOptions>, SecurityOptionsValidator>();
        builder.Services.AddSingleton<ApiKeyStore>();
        builder.Services.AddAuthentication(BearerSchemeName)
            .AddPolicyScheme(BearerSchemeName, "OTLP bearer credential", policy =>
                policy.ForwardDefaultSelector = context =>
                {
                    var mode = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.Authentication.Mode;
                    return mode switch
                    {
                        OtlpAuthenticationMode.Jwt => JwtBearerSetup.SchemeName,
                        OtlpAuthenticationMode.ApiKey | OtlpAuthenticationMode.Jwt =>
                            JwtBearerSetup.LooksLikeJwt(context.Request.Headers.Authorization)
                                ? JwtBearerSetup.SchemeName
                                : ApiKeyAuthenticationHandler.SchemeName,
                        _ => ApiKeyAuthenticationHandler.SchemeName,
                    };
                })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
            .AddJwtBearer(JwtBearerSetup.SchemeName, _ => { });
        JwtBearerSetup.AddOptions(builder.Services);
        builder.Services.AddAuthorization();

        builder.Services.AddHostedService<SecurityStartupReport>();
        return builder;
    }

    /// <summary>The default (policy) authentication scheme; forwards to the API-key or JWT scheme.</summary>
    public const string BearerSchemeName = "OtlpBearer";

    /// <summary>Authentication + authorization middleware: they run after routing, before any endpoint.</summary>
    public static WebApplication UseOtlpSecurity(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>
    /// Unless Mode is None, makes the endpoint require an authenticated client. Applied to every
    /// OTLP endpoint (gRPC service and HTTP /v1/logs).
    /// <code>
    ///  not authenticated                       → challenge → UNAUTHENTICATED / 401 (or UNAVAILABLE / 503 if the STS is down)
    ///  JWT without every RequiredScope         → forbid    → PERMISSION_DENIED / 403
    ///  authenticated (API key, or JWT + scopes) → the endpoint runs
    /// </code>
    /// </summary>
    public static TBuilder RequireOtlpAuthentication<TBuilder>(this TBuilder endpoint, WebApplication app)
        where TBuilder : IEndpointConventionBuilder
    {
        var authentication = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value.Authentication;
        if (authentication.Mode == OtlpAuthenticationMode.None)
        {
            return endpoint;
        }

        var requiredScopes = authentication.Jwt.RequiredScopes;
        var policy = new AuthorizationPolicyBuilder(BearerSchemeName)
            .RequireAuthenticatedUser()
            // Scopes are a JWT concept; API-key clients are allowed once authenticated.
            .RequireAssertion(context =>
                context.User.HasClaim(ApiKeyAuthenticationHandler.AuthMethodClaim, "apikey") ||
                JwtBearerSetup.MissingScopes(context.User, requiredScopes).Count == 0)
            .Build();

        endpoint.RequireAuthorization(policy);
        return endpoint;
    }
}
