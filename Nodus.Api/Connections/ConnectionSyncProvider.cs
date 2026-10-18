using Microsoft.EntityFrameworkCore;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Sync;

namespace Nodus.Api.Connections;

/// <summary>Connections from the recipient's point of view; removed ones as tombstones.</summary>
public sealed class ConnectionSyncProvider(NodusDbContext db, ConnectionService connections) : ISyncEntityProvider
{
    public SyncEntityType EntityType => SyncEntityType.Connection;

    public async Task<IReadOnlyDictionary<string, SyncEntityState>> LoadAsync(Guid userId, IReadOnlyCollection<string> entityIds,
        CancellationToken ct)
    {
        var ids = SyncIds.Guids(entityIds);
        var rows = await db.Connections.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && (c.UserLowId == userId || c.UserHighId == userId))
            .ToListAsync(ct);
        return (await connections.DtosAsync(rows, userId, ct)).ToDictionary(c => c.Id.ToString(), c =>
            c.Status == ConnectionStatus.Removed ? SyncEntityState.Tombstone(c.Version) : SyncEntityState.Upsert(c.Version, c));
    }
}
