using Microsoft.EntityFrameworkCore;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Recurrence;
using Nodus.Api.Sync;

namespace Nodus.Api.Nodes;

public static class TimezoneEndpoints
{
    public static void MapTimezoneEndpoints(this RouteGroupBuilder v1) =>
        v1.MapPut("/me/timezone", ChangeAsync).WithTags("Auth").WithIdempotency();

    /// <summary>
    /// The device moved to another time zone: store it and adapt the user's nodes in one transaction.
    /// Repeating the call with the same zone changes nothing.
    /// </summary>
    private static async Task<ApiResult> ChangeAsync(ChangeTimezoneRequest request, HttpContext http, NodusDbContext db,
        NodeService nodes, UnitOfWork unitOfWork, TimeProvider time, CancellationToken ct)
    {
        if (!ZonedTime.TryFindZone(request.Timezone, out _))
            return new ValidationErrors().Add("timezone", "A valid IANA time zone is required.").ToResult();

        var userId = http.User.GetUserId();
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.Timezone != request.Timezone)
        {
            user.Timezone = request.Timezone;
            user.UpdatedAt = time.GetUtcNow();
            user.Version++;
            unitOfWork.Journal.Record(SyncEntityType.User, user.Id.ToString(), user.Version, SyncOperation.Upsert, [user.Id]);
        }

        var result = await nodes.AdaptToTimezoneAsync(userId, request.Timezone!, ct);
        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(result);
    }
}
