using Microsoft.EntityFrameworkCore;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Sync;

namespace Nodus.Api.AiContinuations;

/// <summary>Only ready/accepted/dismissed suggestions are visible; skipped and stale ones are tombstones.</summary>
public sealed class SuggestionSyncProvider(NodusDbContext db) : ISyncEntityProvider
{
    public SyncEntityType EntityType => SyncEntityType.Suggestion;

    public async Task<IReadOnlyDictionary<string, SyncEntityState>> LoadAsync(Guid userId, IReadOnlyCollection<string> entityIds,
        CancellationToken ct)
    {
        var ids = SyncIds.Guids(entityIds);
        var suggestions = await db.NodeSuggestions.AsNoTracking().Where(s => ids.Contains(s.Id) && s.UserId == userId).ToListAsync(ct);
        return suggestions.ToDictionary(s => s.Id.ToString(), s => SuggestionDto.IsVisible(s)
            ? SyncEntityState.Upsert(s.Version, SuggestionDto.From(s))
            : SyncEntityState.Tombstone(s.Version));
    }
}
