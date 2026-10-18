using Microsoft.EntityFrameworkCore;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Sync;

namespace Nodus.Api.Plans;

public sealed record AiConsentRequest(bool? Granted);

public static class AiConsentEndpoints
{
    public static void MapAiConsentEndpoints(this RouteGroupBuilder v1) =>
        v1.MapPut("/me/ai-consent", ChangeAsync).WithTags("Auth").WithIdempotency();

    /// <summary>
    /// Grants or withdraws consent to send the user's content to AI providers. Repeating a grant keeps the moment of
    /// the first one; the change reaches the user's other devices through sync.
    /// </summary>
    private static async Task<ApiResult> ChangeAsync(AiConsentRequest request, HttpContext http, NodusDbContext db,
        UnitOfWork unitOfWork, FeatureAccess access, TimeProvider time, CancellationToken ct)
    {
        if (request.Granted is not { } granted)
            return new ValidationErrors().Add("granted", "Must be true or false.").ToResult();

        var userId = http.User.GetUserId();
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (granted == (user.AiConsentAt is not null)) return ApiResult.Ok(UserProfile.From(user, access));

        var now = time.GetUtcNow();
        user.AiConsentAt = granted ? now : null;
        user.UpdatedAt = now;
        user.Version++;
        unitOfWork.Journal.Record(SyncEntityType.User, user.Id.ToString(), user.Version, SyncOperation.Upsert, [user.Id]);
        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(UserProfile.From(user, access));
    }
}
