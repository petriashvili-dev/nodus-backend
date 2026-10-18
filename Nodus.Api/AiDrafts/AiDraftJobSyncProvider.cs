using Microsoft.EntityFrameworkCore;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Sync;

namespace Nodus.Api.AiDrafts;

public sealed class AiDraftJobSyncProvider(NodusDbContext db, AiDraftService drafts) : ISyncEntityProvider
{
    public SyncEntityType EntityType => SyncEntityType.AiDraftJob;

    public async Task<IReadOnlyDictionary<string, SyncEntityState>> LoadAsync(Guid userId, IReadOnlyCollection<string> entityIds,
        CancellationToken ct)
    {
        var ids = SyncIds.Guids(entityIds);
        var jobs = await db.AiDraftJobs.AsNoTracking().Where(j => ids.Contains(j.Id) && j.UserId == userId).ToListAsync(ct);
        return (await drafts.DtosAsync(jobs, ct)).ToDictionary(j => j.Id.ToString(), j => SyncEntityState.Upsert(j.Version, j));
    }
}
