using Microsoft.EntityFrameworkCore;
using Nodus.Api.Common;
using Nodus.Api.Connections;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Sync;

namespace Nodus.Api.Authentication;

public sealed record RefreshRequest(string RefreshToken);

public sealed record EmailStartRequest(string Email);

public sealed record EmailVerifyRequest(string Email, string Code);

public sealed record UpdateProfileRequest(string? DisplayName);

public sealed record UserProfile(
    Guid Id,
    bool IsAnonymous,
    string? Email,
    DateTimeOffset? EmailVerifiedAt,
    string? DisplayName,
    long Version,
    DateTimeOffset CreatedAt)
{
    public const int DisplayNameLength = 60;

    public static UserProfile From(User user) =>
        new(user.Id, user.IsAnonymous, user.Email, user.EmailVerifiedAt, user.DisplayName, user.Version, user.CreatedAt);
}

public static class AuthEndpoints
{
    private const string CodeSentMessage = "If the request is valid, a verification code has been sent.";

    public static void MapAuthEndpoints(this RouteGroupBuilder v1)
    {
        var auth = v1.MapGroup("/auth").WithTags("Auth");
        auth.MapPost("/anonymous", CreateAnonymousAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Auth);
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Auth);
        auth.MapPost("/logout", LogoutAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Auth);
        auth.MapPost("/email/start", StartLinkAsync).RequireRateLimiting(RateLimits.Otp);
        auth.MapPost("/email/verify", VerifyLinkAsync).RequireRateLimiting(RateLimits.Otp);
        auth.MapPost("/email/sign-in/start", StartSignInAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Otp);
        auth.MapPost("/email/sign-in/verify", SignInAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Otp);

        v1.MapGet("/me", MeAsync).WithTags("Auth");
        v1.MapPatch("/me", UpdateProfileAsync).WithTags("Auth").WithIdempotency();
        // No Idempotency-Key: the idempotency records are deleted with the account. A repeat after a lost response
        // gets 401 (the token's account is gone), which the client treats as "deleted".
        v1.MapDelete("/me", (HttpContext http, AccountDeletionService deletion, CancellationToken ct) =>
            deletion.DeleteAsync(http.User.GetUserId(), ct)).WithTags("Auth");
    }

    private static async Task<ApiResult> CreateAnonymousAsync(
        NodusDbContext db, ITokenService tokens, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var user = new User { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        db.Users.Add(user);
        var issued = tokens.IssueNewSession(user.Id);
        await db.SaveChangesAsync(ct);
        return ApiResult.Created(issued);
    }

    private static async Task<ApiResult> RefreshAsync(RefreshRequest request, ITokenService tokens, CancellationToken ct) =>
        await tokens.RefreshAsync(request.RefreshToken, ct) is { } issued
            ? ApiResult.Ok(issued)
            : ApiResult.Problem(StatusCodes.Status401Unauthorized, "invalid_refresh_token", "Refresh token is invalid or expired.");

    private static async Task<ApiResult> LogoutAsync(RefreshRequest request, ITokenService tokens, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken)) await tokens.RevokeFamilyAsync(request.RefreshToken, ct);
        return ApiResult.NoContent();
    }

    private static async Task<ApiResult> StartLinkAsync(
        EmailStartRequest request, HttpContext http, IEmailOtpService otp, CancellationToken ct)
    {
        if (!EmailAddress.TryNormalize(request.Email, out var email)) return InvalidEmail();
        await otp.RequestAsync(email, EmailCodePurpose.Link, http.User.GetUserId(), ct);
        return ApiResult.Accepted(new { message = CodeSentMessage });
    }

    /// <summary>Attaches a verified email to the same <c>UserId</c>; no data is moved between accounts.</summary>
    private static async Task<ApiResult> VerifyLinkAsync(
        EmailVerifyRequest request, HttpContext http, NodusDbContext db, IEmailOtpService otp, UnitOfWork unitOfWork,
        TimeProvider time, CancellationToken ct)
    {
        if (!EmailAddress.TryNormalize(request.Email, out var email)) return InvalidEmail();
        var userId = http.User.GetUserId();
        if (!await otp.VerifyAsync(email, request.Code ?? "", EmailCodePurpose.Link, userId, ct)) return InvalidCode();

        if (await db.Users.AnyAsync(u => u.Email == email && u.Id != userId, ct))
        {
            await db.SaveChangesAsync(ct);
            return EmailTaken();
        }

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var now = time.GetUtcNow();
        user.Email = email;
        user.EmailVerifiedAt = now;
        user.IsAnonymous = false;
        user.UpdatedAt = now;
        user.Version++;
        unitOfWork.Journal.Record(SyncEntityType.User, user.Id.ToString(), user.Version, SyncOperation.Upsert, [user.Id]);
        try
        {
            await unitOfWork.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            return EmailTaken();
        }

        return ApiResult.Ok(UserProfile.From(user));
    }

    private static async Task<ApiResult> StartSignInAsync(EmailStartRequest request, IEmailOtpService otp, CancellationToken ct)
    {
        if (!EmailAddress.TryNormalize(request.Email, out var email)) return InvalidEmail();
        await otp.RequestAsync(email, EmailCodePurpose.SignIn, null, ct);
        return ApiResult.Accepted(new { message = CodeSentMessage });
    }

    /// <summary>
    /// Signs in to the account that owns the email (new device or reinstall). The anonymous account the device
    /// may have used before is not merged; the client discards it after a successful sign-in.
    /// </summary>
    private static async Task<ApiResult> SignInAsync(
        EmailVerifyRequest request, NodusDbContext db, IEmailOtpService otp, ITokenService tokens, CancellationToken ct)
    {
        if (!EmailAddress.TryNormalize(request.Email, out var email)) return InvalidEmail();
        if (!await otp.VerifyAsync(email, request.Code ?? "", EmailCodePurpose.SignIn, null, ct)) return InvalidCode();

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            await db.SaveChangesAsync(ct);
            return InvalidCode();
        }

        var issued = tokens.IssueNewSession(user.Id);
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok(issued);
    }

    private static async Task<ApiResult> MeAsync(HttpContext http, NodusDbContext db, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? ApiResult.NotFound("User") : ApiResult.Ok(UserProfile.From(user));
    }

    /// <summary>The display name is visible to connections, so their feeds receive the change too.</summary>
    private static async Task<ApiResult> UpdateProfileAsync(UpdateProfileRequest request, HttpContext http, NodusDbContext db,
        UnitOfWork unitOfWork, TimeProvider time, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        if (name?.Length > UserProfile.DisplayNameLength)
            return new ValidationErrors().Add("display_name", $"At most {UserProfile.DisplayNameLength} characters.").ToResult();

        var userId = http.User.GetUserId();
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.DisplayName == name) return ApiResult.Ok(UserProfile.From(user));

        user.DisplayName = name;
        user.UpdatedAt = time.GetUtcNow();
        user.Version++;
        unitOfWork.Journal.Record(SyncEntityType.User, user.Id.ToString(), user.Version, SyncOperation.Upsert, [user.Id]);
        var connections = await db.Connections
            .Where(c => (c.UserLowId == userId || c.UserHighId == userId) && c.Status != ConnectionStatus.Removed)
            .ToListAsync(ct);
        foreach (var connection in connections)
            unitOfWork.Journal.Record(SyncEntityType.Connection, connection.Id.ToString(), connection.Version,
                SyncOperation.Upsert, [connection.Other(userId)]);

        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(UserProfile.From(user));
    }

    private static ApiResult InvalidEmail() =>
        new ValidationErrors().Add("email", "A valid email address is required.").ToResult();

    private static ApiResult InvalidCode() =>
        ApiResult.Problem(StatusCodes.Status400BadRequest, "invalid_code", "The code is invalid or has expired.");

    private static ApiResult EmailTaken() =>
        ApiResult.Problem(StatusCodes.Status409Conflict, "email_already_linked",
            "This email is already linked to another account.",
            "Sign in with this email instead; accounts are not merged automatically.");
}
