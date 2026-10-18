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

public sealed class EmailOtpOptions : IValidatableObject
{
    public const string Section = "EmailOtp";

    [Range(1, 60)] public int CodeExpiryMinutes { get; init; } = 10;
    [Range(0, 3600)] public int ResendCooldownSeconds { get; init; } = 60;
    [Range(1, 100)] public int MaxCodesPerHour { get; init; } = 5;
    [Range(1, 20)] public int MaxAttempts { get; init; } = 5;

    /// <summary>Secret mixed into code hashes so that a database leak does not reveal six-digit codes.</summary>
    [Required, MinLength(16)] public string HashPepper { get; init; } = "";

    /// <summary>
    /// App Review demo accounts: codes for these addresses are <see cref="ReviewCode"/> and are not emailed, because the
    /// reviewer has no access to the mailbox. Empty outside a review; the code is a secret supplied through environment.
    /// </summary>
    public string[]? ReviewEmails { get; init; }

    public string ReviewCode { get; init; } = "";

    public bool IsReviewEmail(string normalizedEmail) =>
        ReviewEmails?.Any(e => EmailAddress.TryNormalize(e, out var review) && review == normalizedEmail) == true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ReviewEmails is not { Length: > 0 }) yield break;
        if (ReviewCode is not { Length: 6 } || !ReviewCode.All(char.IsAsciiDigit))
            yield return new ValidationResult("ReviewCode must be six digits when ReviewEmails are set.", [nameof(ReviewCode)]);
        if (ReviewEmails.Any(e => !EmailAddress.TryNormalize(e, out _)))
            yield return new ValidationResult("ReviewEmails must be valid email addresses.", [nameof(ReviewEmails)]);
    }
}
