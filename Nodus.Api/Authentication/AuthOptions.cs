using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Nodus.Api.Authentication;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    [Required] public string Issuer { get; init; } = "nodus";
    [Required] public string Audience { get; init; } = "nodus-ios";

    /// <summary>HMAC-SHA256 key, at least 32 bytes. Supplied through environment/secrets only.</summary>
    [Required, MinLength(32)] public string SigningKey { get; init; } = "";

    [Range(1, 1440)] public int AccessTokenMinutes { get; init; } = 15;
    [Range(1, 365)] public int RefreshTokenDays { get; init; } = 60;

    /// <summary>
    /// A just-rotated token is still accepted for this long: the refresh response may have been lost
    /// (network drop, app suspended) and the client retries with the previous token.
    /// </summary>
    [Range(0, 600)] public int RotationGraceSeconds { get; init; } = 120;

    public SymmetricSecurityKey GetSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public sealed class EmailOtpOptions
{
    public const string Section = "EmailOtp";

    [Range(1, 60)] public int CodeExpiryMinutes { get; init; } = 10;
    [Range(0, 3600)] public int ResendCooldownSeconds { get; init; } = 60;
    [Range(1, 100)] public int MaxCodesPerHour { get; init; } = 5;
    [Range(1, 20)] public int MaxAttempts { get; init; } = 5;

    /// <summary>Secret mixed into code hashes so that a database leak does not reveal six-digit codes.</summary>
    [Required, MinLength(16)] public string HashPepper { get; init; } = "";
}
