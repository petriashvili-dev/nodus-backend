using Microsoft.EntityFrameworkCore;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Occurrences;

namespace Nodus.Api.Nodes;

/// <summary>
/// The single place that decides who may see and change a node and who receives its sync changes.
/// Every node endpoint and the sync feed go through it.
/// </summary>
public sealed class NodeAccess(NodusDbContext db)
{
    /// <summary>Nodes the user may read and edit, including soft-deleted ones (callers filter as needed).</summary>
    public IQueryable<Node> Accessible(Guid userId) => db.Nodes.Where(n => n.OwnerId == userId);

    public Task<Node?> FindAsync(Guid userId, Guid nodeId, CancellationToken ct) =>
        Accessible(userId).SingleOrDefaultAsync(n => n.Id == nodeId, ct);

    /// <summary>Everyone whose sync feed must receive changes of this node.</summary>
    public Task<IReadOnlyList<Guid>> RecipientsAsync(Node node, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>([node.OwnerId]);

    public static bool CanDelete(Node node, Guid userId) => node.OwnerId == userId;
}

/// <summary>Bulk loading of revisions and states for projections.</summary>
public sealed class NodeHistoryLoader(NodusDbContext db)
{
    public async Task<NodeHistory> LoadAsync(Node node, CancellationToken ct) =>
        (await LoadAsync([node], ct))[node.Id];

    public async Task<IReadOnlyDictionary<Guid, NodeHistory>> LoadAsync(IReadOnlyCollection<Node> nodes, CancellationToken ct)
    {
        var ids = nodes.Select(n => n.Id).ToArray();
        var seriesIds = nodes.Where(n => n.IsSeries).Select(n => n.Id).ToArray();
        var revisions = seriesIds.Length == 0
            ? []
            : await db.NodeRevisions.AsNoTracking().Where(r => seriesIds.Contains(r.NodeId)).ToListAsync(ct);
        var states = await db.NodeOccurrenceStates.AsNoTracking().Where(s => ids.Contains(s.NodeId)).ToListAsync(ct);

        var revisionsByNode = revisions.ToLookup(r => r.NodeId);
        var statesByNode = states.ToLookup(s => s.NodeId);
        return nodes.ToDictionary(n => n.Id, n => new NodeHistory(
            n,
            revisionsByNode[n.Id].OrderBy(r => r.Revision).ToList(),
            statesByNode[n.Id].ToDictionary(s => s.OccurrenceKey, StringComparer.Ordinal)));
    }
}
