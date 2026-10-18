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
    /// <summary>
    /// Nodes the user owns or is a member of, including soft-deleted ones (callers filter as needed).
    /// Owner and members read, edit and untie/retie equally; membership management and deletion are owner-only.
    /// </summary>
    public IQueryable<Node> Accessible(Guid userId) =>
        db.Nodes.Where(n => n.OwnerId == userId || db.SharedNodeMembers.Any(m => m.NodeId == n.Id && m.UserId == userId));

    public Task<Node?> FindAsync(Guid userId, Guid nodeId, CancellationToken ct) =>
        Accessible(userId).SingleOrDefaultAsync(n => n.Id == nodeId, ct);

    /// <summary>Everyone whose sync feed must receive changes of this node.</summary>
    public async Task<IReadOnlyList<Guid>> RecipientsAsync(Node node, CancellationToken ct) =>
    [
        node.OwnerId,
        .. await db.SharedNodeMembers.Where(m => m.NodeId == node.Id).Select(m => m.UserId).ToListAsync(ct)
    ];

    /// <summary>Members of several nodes in one query (owners are not included).</summary>
    public async Task<ILookup<Guid, Guid>> MembersAsync(IReadOnlyCollection<Guid> nodeIds, CancellationToken ct) =>
        (await db.SharedNodeMembers.Where(m => nodeIds.Contains(m.NodeId)).Select(m => new { m.NodeId, m.UserId }).ToListAsync(ct))
        .ToLookup(m => m.NodeId, m => m.UserId);

    public static bool CanDelete(Node node, Guid userId) => node.OwnerId == userId;
}

/// <summary>
/// Bulk loading of revisions, states and members for projections.
/// Occurrence states accumulate with history (a daily habit adds ~365 a year), so only states dated from
/// <c>statesFrom</c> on are loaded — plus every <c>once</c> and undated state. Future states are always loaded
/// (they are few: occurrences untied in advance), so "next occurrence" lookups stay correct.
/// </summary>
public sealed class NodeHistoryLoader(NodusDbContext db, TimeProvider time)
{
    /// <summary>
    /// Margin for "current" views (DTO horizon, feed): they start yesterday in the node's zone, and zones differ from
    /// UTC by up to 14 hours.
    /// </summary>
    private const int CurrentViewMarginDays = 3;

    public DateOnly CurrentViewFrom => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-CurrentViewMarginDays);

    /// <summary>States for current views (see <see cref="CurrentViewFrom"/>).</summary>
    public async Task<NodeHistory> LoadAsync(Node node, CancellationToken ct) =>
        (await LoadAsync([node], CurrentViewFrom, ct))[node.Id];

    public async Task<IReadOnlyDictionary<Guid, NodeHistory>> LoadAsync(IReadOnlyCollection<Node> nodes, CancellationToken ct) =>
        await LoadAsync(nodes, CurrentViewFrom, ct);

    public async Task<NodeHistory> LoadAsync(Node node, DateOnly? statesFrom, CancellationToken ct) =>
        (await LoadAsync([node], statesFrom, ct))[node.Id];

    /// <param name="statesFrom">Earliest occurrence date whose state is needed; null loads the whole history.</param>
    public async Task<IReadOnlyDictionary<Guid, NodeHistory>> LoadAsync(IReadOnlyCollection<Node> nodes, DateOnly? statesFrom,
        CancellationToken ct)
    {
        var ids = nodes.Select(n => n.Id).ToArray();
        var seriesIds = nodes.Where(n => n.IsSeries).Select(n => n.Id).ToArray();
        var revisions = seriesIds.Length == 0
            ? []
            : await db.NodeRevisions.AsNoTracking().Where(r => seriesIds.Contains(r.NodeId)).ToListAsync(ct);
        var states = await db.NodeOccurrenceStates.AsNoTracking()
            .Where(s => ids.Contains(s.NodeId) &&
                        (statesFrom == null || s.OccurrenceKey == OccurrenceKey.Once || s.Date == null || s.Date >= statesFrom))
            .ToListAsync(ct);

        var members = await db.SharedNodeMembers.AsNoTracking()
            .Where(m => ids.Contains(m.NodeId))
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.NodeId, m.UserId, u.DisplayName, m.AddedAt })
            .ToListAsync(ct);

        var revisionsByNode = revisions.ToLookup(r => r.NodeId);
        var statesByNode = states.ToLookup(s => s.NodeId);
        var membersByNode = members.ToLookup(m => m.NodeId);
        return nodes.ToDictionary(n => n.Id, n => new NodeHistory(
            n,
            revisionsByNode[n.Id].OrderBy(r => r.Revision).ToList(),
            statesByNode[n.Id].ToDictionary(s => s.OccurrenceKey, StringComparer.Ordinal),
            membersByNode[n.Id].OrderBy(m => m.AddedAt).Select(m => new NodeMember(m.UserId, m.DisplayName)).ToList()));
    }
}
