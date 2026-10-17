using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Infrastructure.Resend;

namespace Nodus.Api.Authentication;

public interface IEmailOtpService
{
    /// <summary>
    /// Sends a one-time code unless limits apply. Callers must respond identically whether or not a code was sent,
    /// so the endpoint does not reveal which emails have accounts.
    /// </summary>
    Task RequestAsync(string email, EmailCodePurpose purpose, Guid? userId, CancellationToken ct);

    /// <summary>Checks and consumes a code. Attempt counters are saved immediately; a successful match is saved by the caller.</summary>
    Task<bool> VerifyAsync(string email, string code, EmailCodePurpose purpose, Guid? userId, CancellationToken ct);
}

/// <summary>
/// The Resend verification mechanics that existed for subscription restore (hashed six-digit codes with a pepper,
/// resend cooldown, hourly cap, attempt limit, constant-time comparison), now bound to the account <c>UserId</c>.
/// </summary>
public sealed class EmailOtpService(
    NodusDbContext db,
    IEmailSender sender,
    IOptions<EmailOtpOptions> options,
    TimeProvider time) : IEmailOtpService
{
    private readonly EmailOtpOptions _options = options.Value;

    public async Task RequestAsync(string email, EmailCodePurpose purpose, Guid? userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (purpose == EmailCodePurpose.SignIn && !await db.Users.AnyAsync(u => u.Email == email, ct)) return;
        if (purpose == EmailCodePurpose.Link && await db.Users.AnyAsync(u => u.Id == userId && u.Email == email, ct)) return;

        var recent = db.EmailVerificationCodes.Where(c => c.Email == email);
        if (await recent.AnyAsync(c => c.CreatedAt > now.AddSeconds(-_options.ResendCooldownSeconds), ct)) return;
        if (await recent.CountAsync(c => c.CreatedAt > now.AddHours(-1), ct) >= _options.MaxCodesPerHour) return;

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.EmailVerificationCodes.Add(new EmailVerificationCode
        {
            Id = Guid.NewGuid(), Email = email, Purpose = purpose, UserId = purpose == EmailCodePurpose.Link ? userId : null,
            CodeHash = Hash(purpose, email, code), CreatedAt = now, ExpiresAt = now.AddMinutes(_options.CodeExpiryMinutes)
        });

        var action = purpose == EmailCodePurpose.Link ? "подтверждения email" : "входа";
        await sender.SendAsync(new OutgoingEmail(email, $"Nodus: код {action}",
            $"<p>Ваш код для {action} в Nodus: <strong>{code}</strong></p>" +
            $"<p>Код действует {_options.CodeExpiryMinutes} минут. Если вы его не запрашивали, просто проигнорируйте письмо.</p>"), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> VerifyAsync(string email, string code, EmailCodePurpose purpose, Guid? userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var candidate = await db.EmailVerificationCodes
            .Where(c => c.Email == email && c.Purpose == purpose && c.ConsumedAt == null && c.ExpiresAt > now &&
                        (purpose != EmailCodePurpose.Link || c.UserId == userId))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (candidate is null) return false;

        if (++candidate.Attempts >= _options.MaxAttempts) candidate.ConsumedAt = now;
        var matches = code.Length == 6 && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(candidate.CodeHash), Convert.FromHexString(Hash(purpose, email, code)));
        if (!matches)
        {
            await db.SaveChangesAsync(ct);
            return false;
        }

        candidate.ConsumedAt = now;
        return true;
    }

    private string Hash(EmailCodePurpose purpose, string email, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.HashPepper), Encoding.UTF8.GetBytes($"{purpose}:{email}:{code}")));
}

public static class EmailAddress
{
    public static bool TryNormalize(string? raw, out string email)
    {
        email = raw?.Trim().ToLowerInvariant() ?? "";
        return email.Length is > 3 and <= 320 && MailAddress.TryCreate(email, out var parsed) && parsed.Address == email;
    }
}
