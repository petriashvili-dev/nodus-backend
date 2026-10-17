using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Api.Authentication;

public sealed record AuthTokens(
    Guid UserId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    public string TokenType => "Bearer";
}

public interface ITokenService
{
    /// <summary>Starts a new rotation family. The caller saves the context.</summary>
    AuthTokens IssueNewSession(Guid userId);

    /// <summary>Rotates a refresh token; null when it is unknown, expired, revoked or reused.</summary>
    Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken ct);

    Task RevokeFamilyAsync(string refreshToken, CancellationToken ct);
}

public sealed class TokenService(
    NodusDbContext db,
    IOptions<JwtOptions> options,
    TimeProvider time,
    ILogger<TokenService> logger) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public AuthTokens IssueNewSession(Guid userId) => Issue(userId, Guid.NewGuid(), time.GetUtcNow(), out _);

    public async Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 256) return null;
        var now = time.GetUtcNow();
        var current = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == Hash(refreshToken), ct);
        if (current is null || current.ExpiresAt <= now) return null;

        if (current.RevokedAt is null)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var issued = Issue(current.UserId, current.FamilyId, now, out var replacement);
            // Conditional update: of two concurrent refreshes with the same token only one rotates it.
            var rotated = await db.RefreshTokens
                .Where(t => t.Id == current.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RevokedAt, now)
                    .SetProperty(t => t.RevokedReason, RefreshTokenRevocation.Rotated)
                    .SetProperty(t => t.ReplacedById, replacement.Id), ct);
            if (rotated == 1)
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return issued;
            }

            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            current = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == current.Id, ct);
        }

        if (current.RevokedReason == RefreshTokenRevocation.Rotated &&
            now - current.RevokedAt <= TimeSpan.FromSeconds(_options.RotationGraceSeconds) &&
            !await db.RefreshTokens.AnyAsync(t => t.FamilyId == current.FamilyId && t.RevokedReason != null &&
                                                  t.RevokedReason != RefreshTokenRevocation.Rotated, ct))
        {
            // Lost refresh response: issue another token of the same family instead of treating it as theft.
            var retried = Issue(current.UserId, current.FamilyId, now, out _);
            await db.SaveChangesAsync(ct);
            return retried;
        }

        if (current.RevokedReason == RefreshTokenRevocation.Rotated)
        {
            logger.LogWarning("Refresh token reuse detected for family {FamilyId}; revoking the family", current.FamilyId);
            await RevokeFamilyAsync(current.FamilyId, RefreshTokenRevocation.ReuseDetected, now, ct);
        }

        return null;
    }

    public async Task RevokeFamilyAsync(string refreshToken, CancellationToken ct)
    {
        var token = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == Hash(refreshToken), ct);
        if (token is not null)
            await RevokeFamilyAsync(token.FamilyId, RefreshTokenRevocation.Logout, time.GetUtcNow(), ct);
    }

    private Task RevokeFamilyAsync(Guid familyId, RefreshTokenRevocation reason, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && (t.RevokedAt == null || reason == RefreshTokenRevocation.ReuseDetected))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, t => t.RevokedAt ?? now)
                .SetProperty(t => t.RevokedReason, reason), ct);

    private AuthTokens Issue(Guid userId, Guid familyId, DateTimeOffset now, out RefreshToken stored)
    {
        var refresh = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        stored = new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = userId, FamilyId = familyId, TokenHash = Hash(refresh),
            CreatedAt = now, ExpiresAt = now.AddDays(_options.RefreshTokenDays)
        };
        db.RefreshTokens.Add(stored);

        var accessExpires = now.AddMinutes(_options.AccessTokenMinutes);
        var access = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = accessExpires.UtcDateTime,
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Sid, familyId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            ]),
            SigningCredentials = new SigningCredentials(_options.GetSigningKey(), SecurityAlgorithms.HmacSha256)
        });

        return new AuthTokens(userId, access, accessExpires, refresh, stored.ExpiresAt);
    }

    /// <summary>Refresh tokens carry 256 bits of entropy, so a plain SHA-256 is sufficient.</summary>
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
