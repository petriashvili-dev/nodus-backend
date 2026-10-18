using Microsoft.EntityFrameworkCore;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Sync;

namespace Nodus.Api.Nodes;

/// <summary>Live accessible nodes as <see cref="NodeDto"/>; deleted or no longer accessible ones as tombstones.</summary>
public sealed class NodeSyncProvider(NodeAccess access, NodeHistoryLoader histories, NodeDtoFactory dtos) : ISyncEntityProvider
{
    public SyncEntityType EntityType => SyncEntityType.Node;

    public async Task<IReadOnlyDictionary<string, SyncEntityState>> LoadAsync(Guid userId, IReadOnlyCollection<string> entityIds,
        CancellationToken ct)
    {
        var ids = SyncIds.Guids(entityIds);
        var nodes = await access.Accessible(userId).AsNoTracking().Where(n => ids.Contains(n.Id)).ToListAsync(ct);
        var live = nodes.Where(n => n.DeletedAt is null).ToList();
        var loaded = await histories.LoadAsync(live, ct);
        return nodes.ToDictionary(n => n.Id.ToString(), n => n.DeletedAt is null
            ? SyncEntityState.Upsert(n.Version, dtos.Create(loaded[n.Id]))
            : SyncEntityState.Tombstone(n.Version));
    }
}

/// <summary>
/// Exact occurrence states referenced by the page (entity id <c>{nodeId}:{key}</c>), loaded by key regardless of age.
/// States of nodes the recipient cannot see are skipped: the node tombstone covers them.
/// </summary>
public sealed class OccurrenceStateSyncProvider(NodusDbContext db, NodeAccess access) : ISyncEntityProvider
{
    public SyncEntityType EntityType => SyncEntityType.OccurrenceState;

    public async Task<IReadOnlyDictionary<string, SyncEntityState>> LoadAsync(Guid userId, IReadOnlyCollection<string> entityIds,
        CancellationToken ct)
    {
        var keys = entityIds.Select(Parse).Where(k => k is not null).Select(k => k!.Value).ToList();
        var nodeIds = keys.Select(k => k.NodeId).Distinct().ToArray();
        var visible = (await access.Accessible(userId).Where(n => nodeIds.Contains(n.Id) && n.DeletedAt == null)
            .Select(n => n.Id).ToListAsync(ct)).ToHashSet();
        var occurrenceKeys = keys.Select(k => k.Key).Distinct().ToArray();
        var states = (await db.NodeOccurrenceStates.AsNoTracking()
                .Where(s => visible.Contains(s.NodeId) && occurrenceKeys.Contains(s.OccurrenceKey))
                .ToListAsync(ct))
            .ToDictionary(s => OccurrenceStateDto.EntityId(s.NodeId, s.OccurrenceKey));

        return entityIds.ToDictionary(id => id, id =>
            states.TryGetValue(id, out var state)
                ? SyncEntityState.Upsert(state.Version, OccurrenceStateDto.From(state))
                : SyncEntityState.Nothing);
    }

    private static (Guid NodeId, string Key)? Parse(string entityId)
    {
        var separator = entityId.IndexOf(':');
        return separator > 0 && Guid.TryParse(entityId[..separator], out var nodeId) ? (nodeId, entityId[(separator + 1)..]) : null;
    }
}
