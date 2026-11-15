using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OtlpServer.Tests.Infrastructure;

/// <summary>
/// A throwaway PKI for the TLS tests, generated in memory: a trusted CA that signs the server
/// and client certificates, and a second, untrusted CA for negative tests. PEM files are
/// written to a temp folder, because that's how Kestrel loads certificates from configuration.
/// </summary>
public sealed class TestCertificates : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("otlp-tls-tests-").FullName;

    public TestCertificates()
    {
        (Ca, _) = CreateCa("OTLP Test CA");
        (var server, var serverKey) = Issue(Ca, "localhost", OidServerAuth, includeSan: true);
        (ClientCertificate, _) = Issue(Ca, "billing-service", OidClientAuth);
        (ServerUsedAsClient, _) = Issue(Ca, "not-a-client", OidServerAuth);

        var (untrustedCa, _) = CreateCa("Some Other CA");
        (UntrustedClientCertificate, _) = Issue(untrustedCa, "intruder", OidClientAuth);

        ServerCertificate = server;
        ServerCertPath = Write("server.crt", server.ExportCertificatePem());
        ServerKeyPath = Write("server.key", serverKey.ExportPkcs8PrivateKeyPem());
        CaPath = Write("ca.crt", Ca.ExportCertificatePem());
        EmptyPemPath = Write("empty.pem", "");
    }

    public X509Certificate2 Ca { get; }
    public X509Certificate2 ServerCertificate { get; }
    public X509Certificate2 ClientCertificate { get; }
    public X509Certificate2 UntrustedClientCertificate { get; }

    /// <summary>Signed by the trusted CA, but for SERVER authentication only.</summary>
    public X509Certificate2 ServerUsedAsClient { get; }

    public string ServerCertPath { get; }
    public string ServerKeyPath { get; }
    public string CaPath { get; }
    public string EmptyPemPath { get; }

    private const string OidServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string OidClientAuth = "1.3.6.1.5.5.7.3.2";

    private static (X509Certificate2, RSA) CreateCa(string name)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        var now = DateTimeOffset.UtcNow;
        return (request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1)), key);
    }

    private static (X509Certificate2, RSA) Issue(X509Certificate2 ca, string name, string usageOid, bool includeSan = false)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usageOid)], critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        if (includeSan)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
        }

        var now = DateTimeOffset.UtcNow;
        // The CA certificate already carries its private key (CreateSelfSigned), so it signs directly.
        using var issued = request.Create(ca, now.AddHours(-1), now.AddMonths(6), RandomNumberGenerator.GetBytes(8));
        using var withKey = issued.CopyWithPrivateKey(key);

        // Round-trip through PKCS#12: on macOS, TLS can't use a certificate whose private key
        // only exists in memory ("ephemeral key"); this gives it a proper key container.
        return (X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null), key);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
