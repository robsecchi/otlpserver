using System.Security.Cryptography;
using System.Text;
using Grpc.Core;

namespace OtlpServer.Tests.Infrastructure;

/// <summary>Test API keys and the configuration that enables them.</summary>
public static class ApiKeys
{
    public const string BillingKey = "billing-key-current";
    public const string BillingOldKey = "billing-key-previous"; // second key of the same client: rotation
    public const string FrontendKey = "frontend-key";

    /// <summary>What goes in the configuration: the SHA-256 hash, never the key.</summary>
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static Dictionary<string, string?> Enabled() => new()
    {
        ["Otlp:Security:Authentication:Mode"] = "ApiKey",
        ["Otlp:Security:Authentication:Clients:0:Name"] = "billing-service",
        ["Otlp:Security:Authentication:Clients:0:KeyHashes:0"] = Hash(BillingKey),               // uppercase hex
        ["Otlp:Security:Authentication:Clients:0:KeyHashes:1"] = Hash(BillingOldKey).ToLowerInvariant(), // lowercase hex
        ["Otlp:Security:Authentication:Clients:1:Name"] = "frontend",
        ["Otlp:Security:Authentication:Clients:1:KeyHashes:0"] = Hash(FrontendKey),
    };

    /// <summary>gRPC call options carrying a bearer key (gRPC metadata = HTTP/2 headers).</summary>
    public static CallOptions Grpc(string key) => new(headers: new Metadata { { "authorization", $"Bearer {key}" } });

    public static HttpClient WithKey(this HttpClient http, string key)
    {
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return http;
    }
}
