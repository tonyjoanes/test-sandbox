using System.Security.Cryptography;
using System.Text;

namespace AuthPatterns.Api;

/// <summary>
/// Same algorithm as apim/policies/verify-webhook-hmac.xml: HMAC-SHA256 over "{timestamp}.{body}",
/// a 5 minute replay window, a constant-time compare, and primary/secondary secrets for rotation.
/// APIM is the primary check. The backend can use this as a defence-in-depth check.
/// </summary>
public sealed class HmacVerifier(IEnumerable<string> secrets, TimeProvider clock, TimeSpan? window = null)
{
    private readonly string[] _secrets = secrets.Where(s => !string.IsNullOrEmpty(s)).ToArray();
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(5);

    public static string Sign(string secret, string timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    public bool Verify(string? timestamp, string? signature, string body)
    {
        if (!long.TryParse(timestamp, out var ts)) return false;
        var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(ts);
        if (age.Duration() > _window) return false;
        if (signature is null || !signature.StartsWith("sha256=")) return false;

        var provided = Encoding.ASCII.GetBytes(signature.ToLowerInvariant());
        var ok = false;
        foreach (var secret in _secrets) // check every secret so timing doesn't reveal which one matched
        {
            var expected = Encoding.ASCII.GetBytes(Sign(secret, timestamp!, body));
            ok |= CryptographicOperations.FixedTimeEquals(expected, provided);
        }
        return ok;
    }
}
