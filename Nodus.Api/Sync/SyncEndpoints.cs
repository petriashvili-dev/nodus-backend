using System.Data;
using Microsoft.EntityFrameworkCore;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;

namespace Nodus.Api.Sync;

public sealed record SyncChangeDto(
    long Revision,
    SyncEntityType EntityType,
    string EntityId,
    SyncOperation Operation,
    long Version,
    object? Data);

public static class SyncEndpoints
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    public static void MapSyncEndpoints(this RouteGroupBuilder v1)
    {
        var sync = v1.MapGroup("/sync").WithTags("Sync");
        sync.MapGet("/", DeltaAsync);
        sync.MapGet("/snapshot", SnapshotAsync);
    }

    /// <summary>
    /// Changes after <c>cursor</c> in revision order. Each entity appears once with its current canonical state
    /// (or a tombstone when it was deleted or is no longer accessible). The client applies the page and stores
    /// <c>next_cursor</c> atomically and continues while <c>has_more</c>.
    /// </summary>
    private static async Task<ApiResult> DeltaAsync(long? cursor, int? limit, HttpContext http, NodusDbContext db,
        NodeAccess access, NodeHistoryLoader histories, NodeDtoFactory dtos, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var from = cursor ?? 0;
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult.NotFound("User");
        if (from < user.SyncPrunedRevision || from > user.SyncRevision || from < 0)
            return ApiResult.Problem(StatusCodes.Status410Gone, "cursor_expired",
                "The cursor is no longer valid; load /v1/sync/snapshot and keep the local outbox.",
                extensions: new Dictionary<string, object?> { ["snapshot_url"] = "/v1/sync/snapshot" });

        var page = await db.SyncChanges.AsNoTracking()
            .Where(c => c.RecipientUserId == userId && c.Revision > from)
            .OrderBy(c => c.Revision)
            .Take(take + 1)
            .ToListAsync(ct);
        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(take);

        var latest = page
            .GroupBy(c => (c.EntityType, c.EntityId))
            .Select(g => g.MaxBy(c => c.Revision)!)
            .OrderBy(c => c.Revision)
            .ToList();

        var stateKeys = latest.Where(c => c.EntityType == SyncEntityType.OccurrenceState)
            .Select(c => ParseStateId(c.EntityId)).Where(k => k is not null).Select(k => k!.Value).ToList();
        var nodeIds = latest.Where(c => c.EntityType == SyncEntityType.Node)
            .Select(c => Guid.TryParse(c.EntityId, out var id) ? id : Guid.Empty)
            .Concat(stateKeys.Select(k => k.NodeId)).Distinct().ToArray();
        var nodes = await access.Accessible(userId).AsNoTracking().Where(n => nodeIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        var liveNodes = nodes.Values.Where(n => n.DeletedAt is null).ToList();
        var loaded = await histories.LoadAsync(liveNodes, ct);

        var changes = new List<SyncChangeDto>(latest.Count);
        foreach (var change in latest)
        {
            switch (change.EntityType)
            {
                case SyncEntityType.User when change.EntityId == userId.ToString():
                    changes.Add(new SyncChangeDto(change.Revision, change.EntityType, change.EntityId, SyncOperation.Upsert,
                        user.Version, UserProfile.From(user)));
                    break;
                case SyncEntityType.Node:
                    var node = Guid.TryParse(change.EntityId, out var nodeId) ? nodes.GetValueOrDefault(nodeId) : null;
                    changes.Add(node is { DeletedAt: null }
                        ? new SyncChangeDto(change.Revision, change.EntityType, change.EntityId, SyncOperation.Upsert,
                            node.Version, dtos.Create(loaded[node.Id]))
                        : Tombstone(change, node?.Version));
                    break;
                case SyncEntityType.OccurrenceState:
                    var key = ParseStateId(change.EntityId);
                    if (key is null || !loaded.TryGetValue(key.Value.NodeId, out var history)) break; // the node tombstone covers it
                    if (history.States.TryGetValue(key.Value.Key, out var state))
                        changes.Add(new SyncChangeDto(change.Revision, change.EntityType, change.EntityId, SyncOperation.Upsert,
                            state.Version, OccurrenceStateDto.From(state)));
                    break;
            }
        }

        var next = page.Count > 0 ? page[^1].Revision : from;
        return ApiResult.Ok(new { changes, next_cursor = next, has_more = hasMore });
    }

    /// <summary>Full state for first launch or an expired cursor; the returned cursor continues Delta Sync.</summary>
    private static async Task<ApiResult> SnapshotAsync(HttpContext http, NodusDbContext db, NodeAccess access,
        NodeHistoryLoader histories, NodeDtoFactory dtos, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult.NotFound("User");

        var nodes = await access.Accessible(userId).AsNoTracking().Where(n => n.DeletedAt == null).ToListAsync(ct);
        var loaded = await histories.LoadAsync(nodes, ct);
        await transaction.CommitAsync(ct);

        return ApiResult.Ok(new
        {
            cursor = user.SyncRevision,
            user = UserProfile.From(user),
            nodes = nodes.Select(n => dtos.Create(loaded[n.Id])).ToList(),
            occurrence_states = loaded.Values.SelectMany(h => h.States.Values).Select(OccurrenceStateDto.From).ToList()
        });
    }

    private static SyncChangeDto Tombstone(SyncChange change, long? version) =>
        new(change.Revision, change.EntityType, change.EntityId, SyncOperation.Delete, version ?? change.EntityVersion, null);

    private static (Guid NodeId, string Key)? ParseStateId(string entityId)
    {
        var separator = entityId.IndexOf(':');
        return separator > 0 && Guid.TryParse(entityId[..separator], out var nodeId) ? (nodeId, entityId[(separator + 1)..]) : null;
    }
}
