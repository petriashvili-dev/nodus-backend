namespace Nodus.Api.Authentication;

public enum EmailCodePurpose
{
    /// <summary>Attach an email to the signed-in (usually anonymous) account.</summary>
    Link,

    /// <summary>Sign in to the account that owns a verified email, e.g. after reinstalling the app.</summary>
    SignIn
}

public sealed class EmailVerificationCode
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public EmailCodePurpose Purpose { get; set; }

    /// <summary>The account that requested a <see cref="EmailCodePurpose.Link"/> code.</summary>
    public Guid? UserId { get; set; }

    public required string CodeHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
