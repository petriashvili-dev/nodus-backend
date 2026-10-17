using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Nodus.Api.Infrastructure.Apns;

public sealed class ApnsOptions
{
    public const string Section = "Apns";

    /// <summary>Key ID of the APNs auth key (10 characters, shown next to the key in Apple Developer → Keys).</summary>
    public string KeyId { get; init; } = "";

    /// <summary>Apple Developer Team ID (Membership page).</summary>
    public string TeamId { get; init; } = "";

    /// <summary>App bundle id, sent as <c>apns-topic</c>.</summary>
    public string BundleId { get; init; } = "";

    /// <summary>
    /// Contents of the <c>.p8</c> file: PEM with BEGIN/END lines or bare base64 PKCS#8; literal <c>\n</c>,
    /// whitespace and quotes (as they end up in .env files) are tolerated. Secret: environment/secrets only.
    /// </summary>
    public string PrivateKey { get; init; } = "";

    /// <summary>Gateway tried first for devices whose environment is unknown.</summary>
    public bool PreferSandbox { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(TeamId) &&
        !string.IsNullOrWhiteSpace(BundleId) && !string.IsNullOrWhiteSpace(PrivateKey);
}

/// <summary>
/// Provider JWT for APNs, one per process. Apple accepts a token for up to an hour and rejects refreshing it more
/// often than every 20 minutes (<c>TooManyProviderTokenUpdates</c>), so it is cached for 50 minutes.
/// </summary>
public sealed partial class ApnsTokenProvider(IOptions<ApnsOptions> options, TimeProvider time)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(50);

    private readonly Lock _lock = new();
    private string? _token;
    private DateTimeOffset _expiresAt;

    /// <exception cref="FormatException">The key is not base64.</exception>
    /// <exception cref="CryptographicException">The key is base64 but not a P-256 PKCS#8 key.</exception>
    public string GetToken()
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            if (_token is not null && now < _expiresAt) return _token;
            _token = CreateToken(options.Value, now);
            _expiresAt = now + Lifetime;
            return _token;
        }
    }

    /// <summary>After <c>ExpiredProviderToken</c>; only if a concurrent push has not already replaced it.</summary>
    public void Invalidate(string token)
    {
        lock (_lock)
        {
            if (_token == token) _token = null;
        }
    }

    private static string CreateToken(ApnsOptions apns, DateTimeOffset now)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(DecodePrivateKey(apns.PrivateKey), out _);

        var header = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = apns.KeyId }));
        var payload = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = apns.TeamId, iat = now.ToUnixTimeSeconds() }));
        var signed = $"{header}.{payload}";
        // .NET produces the IEEE P1363 (r||s) signature format that JWS ES256 requires.
        var signature = key.SignData(Encoding.UTF8.GetBytes(signed), HashAlgorithmName.SHA256);
        return $"{signed}.{Base64UrlEncoder.Encode(signature)}";
    }

    internal static byte[] DecodePrivateKey(string privateKey)
    {
        var key = PemBoundary().Replace(privateKey.Replace("\\n", "\n").Replace("\\r", "\r"), string.Empty);
        return Convert.FromBase64String(new string(key.Where(c => !char.IsWhiteSpace(c) && c is not '"' and not '\'').ToArray()));
    }

    [GeneratedRegex("-----(BEGIN|END)[^-]*-----")]
    private static partial Regex PemBoundary();
}
