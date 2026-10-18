using System.Data;
using Microsoft.EntityFrameworkCore;
using Nodus.Api.AiContinuations;
using Nodus.Api.AiDrafts;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Common.Resolvers;
using Nodus.Api.Connections;
using Nodus.Api.DataPortability;
using Nodus.Api.Infrastructure.Observability;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;
using Nodus.Api.Plans;

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
        ISelectionResolver<SyncEntityType, ISyncEntityProvider> providers, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        NodusMetrics.Sync("delta");
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

        // Each entity once, with its latest revision in the page.
        var latest = page
            .GroupBy(c => (c.EntityType, c.EntityId))
            .Select(g => g.MaxBy(c => c.Revision)!)
            .OrderBy(c => c.Revision)
            .ToList();

        // One bulk load per entity type; types without a provider (older journal entries) are skipped.
        var states = new Dictionary<(SyncEntityType, string), SyncEntityState>();
        foreach (var group in latest.GroupBy(c => c.EntityType))
        {
            if (providers.GetOrDefault(group.Key) is not { } provider) continue;
            var loaded = await provider.LoadAsync(userId, group.Select(c => c.EntityId).ToList(), ct);
            foreach (var change in group)
                states[(group.Key, change.EntityId)] = loaded.TryGetValue(change.EntityId, out var state)
                    ? state
                    : SyncEntityState.Tombstone();
        }

        var changes = new List<SyncChangeDto>(latest.Count);
        foreach (var change in latest)
        {
            if (!states.TryGetValue((change.EntityType, change.EntityId), out var state) || state.Skip) continue;
            changes.Add(state.Data is null
                ? new SyncChangeDto(change.Revision, change.EntityType, change.EntityId, SyncOperation.Delete,
                    state.Version ?? change.EntityVersion, null)
                : new SyncChangeDto(change.Revision, change.EntityType, change.EntityId, SyncOperation.Upsert,
                    state.Version ?? change.EntityVersion, state.Data));
        }

        var next = page.Count > 0 ? page[^1].Revision : from;
        return ApiResult.Ok(new { changes, next_cursor = next, has_more = hasMore });
    }

    /// <summary>Full state for first launch or an expired cursor; the returned cursor continues Delta Sync.</summary>
    private static async Task<ApiResult> SnapshotAsync(HttpContext http, NodusDbContext db, NodeAccess access,
        NodeHistoryLoader histories, NodeDtoFactory dtos, ConnectionService connectionService, AiDraftService aiDrafts,
        FeatureAccess features, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        NodusMetrics.Sync("snapshot");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult.NotFound("User");

        var nodes = await access.Accessible(userId).AsNoTracking().Where(n => n.DeletedAt == null).ToListAsync(ct);
        var loaded = await histories.LoadAsync(nodes, ct);
        var connections = await connectionService.ListAsync(userId, ct);
        var draftJobs = await aiDrafts.DtosAsync(await db.AiDraftJobs.AsNoTracking()
            .Where(j => j.UserId == userId && (j.Status == AiDraftJobStatus.Queued || j.Status == AiDraftJobStatus.Processing ||
                                               j.Status == AiDraftJobStatus.Ready))
            .ToListAsync(ct), ct);
        var readySuggestions = await db.NodeSuggestions.AsNoTracking()
            .Where(s => s.UserId == userId && s.Status == SuggestionStatus.Ready)
            .ToListAsync(ct);
        await transaction.CommitAsync(ct);

        return ApiResult.Ok(new
        {
            cursor = user.SyncRevision,
            user = UserProfile.From(user, features),
            nodes = nodes.Select(n => dtos.Create(loaded[n.Id])).ToList(),
            connections,
            ai_draft_jobs = draftJobs,
            suggestions = readySuggestions.Select(SuggestionDto.From).ToList(),
            occurrence_states = loaded.Values.SelectMany(h => h.States.Values).Select(OccurrenceStateDto.From).ToList()
        });
    }
}
