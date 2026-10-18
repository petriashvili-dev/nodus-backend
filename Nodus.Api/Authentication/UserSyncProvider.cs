using Microsoft.EntityFrameworkCore;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Plans;
using Nodus.Api.Sync;

namespace Nodus.Api.Authentication;

/// <summary>The recipient's own profile; other users never appear as <c>user</c> entities.</summary>
public sealed class UserSyncProvider(NodusDbContext db, FeatureAccess access) : ISyncEntityProvider
{
    public SyncEntityType EntityType => SyncEntityType.User;

    public async Task<IReadOnlyDictionary<string, SyncEntityState>> LoadAsync(Guid userId, IReadOnlyCollection<string> entityIds,
        CancellationToken ct)
    {
        var own = userId.ToString();
        var user = entityIds.Contains(own) ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct) : null;
        return entityIds.ToDictionary(id => id, id => id == own && user is not null
            ? SyncEntityState.Upsert(user.Version, UserProfile.From(user, access))
            : SyncEntityState.Nothing);
    }
}
