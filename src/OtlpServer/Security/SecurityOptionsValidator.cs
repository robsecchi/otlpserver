using System.Globalization;
using Microsoft.Extensions.Options;

namespace OtlpServer.Security;

/// <summary>
/// Checks the authentication settings at startup. Every mistake here stops the server with
/// a precise message, because the alternative is worse either way: a typo either locks every
/// client out at runtime, or (with no keys at all) leaves the server effectively open.
/// </summary>
public sealed class SecurityOptionsValidator : IValidateOptions<SecurityOptions>
{
    private const string Section = SecurityOptions.SectionName + ":Authentication";

    public ValidateOptionsResult Validate(string? name, SecurityOptions options)
    {
        var auth = options.Authentication;
        var errors = new List<string>();

        const OtlpAuthenticationMode known = OtlpAuthenticationMode.ApiKey | OtlpAuthenticationMode.Jwt;
        if ((auth.Mode & ~known) != 0)
        {
            // The binder turns any number into the enum (e.g. "7"); only known flags are allowed.
            errors.Add($"{Section}:Mode must be None, ApiKey, Jwt, or \"ApiKey, Jwt\".");
            return ValidateOptionsResult.Fail(errors);
        }

        if (auth.Mode.HasFlag(OtlpAuthenticationMode.ApiKey))
        {
            ValidateApiKeys(auth, errors);
        }
        if (auth.Mode.HasFlag(OtlpAuthenticationMode.Jwt))
        {
            ValidateJwt(auth.Jwt, errors);
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateJwt(JwtAuthenticationOptions jwt, List<string> errors)
    {
        const string where = Section + ":Jwt";

        if (!Uri.TryCreate(jwt.Authority, UriKind.Absolute, out var authority))
        {
            errors.Add($"{where}:Authority must be the token issuer's absolute URL (Keycloak: https://<host>/realms/<realm>).");
        }
        else if (jwt.RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{where}:Authority must use https. For a local development STS only, set {where}:RequireHttpsMetadata to false.");
        }

        if (jwt.Audiences.Count == 0 || jwt.Audiences.Any(string.IsNullOrWhiteSpace))
        {
            // Without it, a token the same STS issued for ANY application would be accepted.
            errors.Add($"{where}:Audiences must list the audience(s) tokens for this server carry (aud claim).");
        }

        if (string.IsNullOrWhiteSpace(jwt.ClientNameClaim))
        {
            errors.Add($"{where}:ClientNameClaim is empty.");
        }
        if (jwt.ClockSkew < TimeSpan.Zero)
        {
            errors.Add($"{where}:ClockSkew can't be negative.");
        }
        if (jwt.UnavailableRetryDelay <= TimeSpan.Zero)
        {
            errors.Add($"{where}:UnavailableRetryDelay must be positive.");
        }
    }

    private static void ValidateApiKeys(OtlpAuthenticationOptions auth, List<string> errors)
    {
        if (auth.Clients.Count == 0)
        {
            // No silent fallback to "open": ApiKey with no keys refuses to start.
            errors.Add($"{Section}:Mode includes ApiKey, but no Clients are configured, so no API key could ever be valid.");
        }

        var namesSeen = new HashSet<string>(StringComparer.Ordinal);
        var hashesSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < auth.Clients.Count; i++)
        {
            var client = auth.Clients[i];
            var where = $"{Section}:Clients:{i}";

            if (string.IsNullOrWhiteSpace(client.Name))
            {
                errors.Add($"{where}:Name is empty.");
            }
            else if (!namesSeen.Add(client.Name))
            {
                errors.Add($"{where}:Name '{client.Name}' is used by more than one client.");
            }

            if (client.KeyHashes.Count == 0)
            {
                errors.Add($"{where} ('{client.Name}') has no KeyHashes.");
            }

            for (var k = 0; k < client.KeyHashes.Count; k++)
            {
                var hash = client.KeyHashes[k];
                if (!IsSha256Hex(hash))
                {
                    // Most likely the key itself was pasted instead of its hash.
                    errors.Add($"{where}:KeyHashes:{k} is not a SHA-256 hash (64 hex characters). " +
                               "Store the hash of the key, e.g. printf %s \"$KEY\" | shasum -a 256.");
                }
                else if (hashesSeen.TryGetValue(hash, out var owner) && owner != client.Name)
                {
                    // One key, two identities: which client would it be?
                    errors.Add($"{where}:KeyHashes:{k} is also a key of client '{owner}'. A key must identify one client.");
                }
                else
                {
                    hashesSeen[hash] = client.Name;
                }
            }
        }
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(c => char.IsAsciiHexDigit(c));
}
