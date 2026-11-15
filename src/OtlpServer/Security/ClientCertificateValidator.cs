using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OtlpServer.Security;

/// <summary>
/// Checks a client certificate against a CUSTOM set of trusted CAs (e.g. your internal CA),
/// instead of the operating system's trust store. Kestrel can require client certificates from
/// configuration alone, but it can only check them against the OS store; this is the bit of
/// code that makes "only certificates signed by OUR CA" possible.
///
/// <code>
///  TLS handshake ──► client sends its certificate (+ intermediates)
///                ──► Kestrel calls ClientCertificateValidation(cert, chain, errors)
///                ──► Validate(): build a chain to one of OUR roots, require "client auth" usage
///                      ├─ ok   → connection accepted
///                      └─ fail → handshake fails: no HTTP request ever reaches the app
/// </code>
/// </summary>
public sealed class ClientCertificateValidator(X509Certificate2Collection trustedRoots, ILogger logger)
{
    // id-kp-clientAuth: the certificate is meant to authenticate a CLIENT. A server certificate
    // signed by the same CA must not be usable as a client identity. (A certificate with no
    // extended-key-usage extension at all is valid for any purpose, as X.509 defines.)
    private static readonly Oid ClientAuthentication = new("1.3.6.1.5.5.7.3.2");

    /// <summary>Loads every certificate from a PEM bundle. Fails (so startup fails) if the file is missing or has none.</summary>
    public static X509Certificate2Collection LoadTrustedRoots(string pemPath)
    {
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(pemPath);
        if (roots.Count == 0)
        {
            throw new InvalidOperationException($"No certificate found in client CA file '{pemPath}'.");
        }
        return roots;
    }

    /// <summary>The ClientCertificateValidation callback for Kestrel's HTTPS options.</summary>
    public bool Validate(X509Certificate2 certificate, X509Chain? presentedChain, SslPolicyErrors _)
    {
        // The errors Kestrel computed are against the OS store, so they're ignored: we build
        // our own chain instead.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots);
        chain.ChainPolicy.ApplicationPolicy.Add(ClientAuthentication);

        // Revocation (CRL / OCSP) isn't checked: an internal CA often doesn't publish one, and
        // an unreachable CRL would block every client. Remove a client by rotating the CA or
        // (once authentication exists) by revoking its key.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        // Intermediates the client sent along, so the chain can reach our root through them.
        if (presentedChain is not null)
        {
            foreach (var element in presentedChain.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        if (chain.Build(certificate))
        {
            return true;
        }

        logger.LogWarning(
            "Rejected client certificate '{Subject}' (thumbprint {Thumbprint}): {Problems}",
            certificate.Subject, certificate.Thumbprint,
            string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Distinct()));
        return false;
    }
}
