using Microsoft.EntityFrameworkCore;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Sync;

namespace Nodus.Api.DataPortability;

public sealed class ImportJobSyncProvider(NodusDbContext db, ImportService imports) : ISyncEntityProvider
{
    public SyncEntityType EntityType => SyncEntityType.ImportJob;

    public async Task<IReadOnlyDictionary<string, SyncEntityState>> LoadAsync(Guid userId, IReadOnlyCollection<string> entityIds,
        CancellationToken ct)
    {
        var ids = SyncIds.Guids(entityIds);
        var jobs = await db.ImportJobs.AsNoTracking().Where(j => ids.Contains(j.Id) && j.UserId == userId).ToListAsync(ct);
        return jobs.ToDictionary(j => j.Id.ToString(), j => SyncEntityState.Upsert(j.Version, imports.Dto(j)));
    }
}
