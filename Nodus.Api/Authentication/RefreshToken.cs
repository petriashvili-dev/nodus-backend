namespace Nodus.Api.Authentication;

public enum RefreshTokenRevocation
{
    Rotated,
    Logout,
    ReuseDetected
}

/// <summary>
/// One refresh token of a rotation family (a login session on one installation). Only the SHA-256 hash is stored.
/// Presenting an already rotated token outside the grace period revokes the whole family.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public RefreshTokenRevocation? RevokedReason { get; set; }
    public Guid? ReplacedById { get; set; }
}
