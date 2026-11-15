using Microsoft.AspNetCore.Server.Kestrel.Https;

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

        builder.Services.AddHostedService<SecurityStartupReport>();
        return builder;
    }
}
