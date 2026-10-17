using Microsoft.EntityFrameworkCore;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;
using Nodus.Api.Occurrences;

namespace Nodus.Api.DataPortability;

/// <summary>
/// Builds the export in the current format. Only the user's own active nodes are exported: no deleted or done
/// one-time nodes, no ended series, no nodes of other owners the user is a member of.
/// </summary>
public sealed class ExportService(NodusDbContext db, TimeProvider time)
{
    public async Task<NodusDocumentV1> ExportAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.OwnerId == userId && n.DeletedAt == null)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct);
        var done = (await db.NodeOccurrenceStates.AsNoTracking()
                .Where(s => s.OccurrenceKey == OccurrenceKey.Once && s.State == OccurrenceState.Untied &&
                            db.Nodes.Any(n => n.Id == s.NodeId && n.OwnerId == userId))
                .Select(s => s.NodeId)
                .ToListAsync(ct))
            .ToHashSet();

        return new NodusDocumentV1(ImportFormat.NodusSource, ImportFormat.CurrentVersion, now, user.Timezone,
            nodes.Where(n => ActiveNodes.IsActive(n.Type, n.Date, n.Time, n.Recurrence, n.Timezone, now, done.Contains(n.Id)))
                .Select(n => new NodusNodeV1(n.Type, n.Title, n.Description, n.Timezone, n.Date, n.Time,
                    n.Type == NodeType.Recurring ? n.Recurrence : null,
                    n.Type == NodeType.Anniversary ? n.TrackYears : null))
                .ToList());
    }
}
