using Microsoft.EntityFrameworkCore;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;
using Nodus.Api.Recurrence;

namespace Nodus.Api.Occurrences;

public sealed record FeedItemDto(NodeType NodeType, long NodeVersion, OccurrenceDto Occurrence);

public static class OccurrenceEndpoints
{
    private const int MaxRangeDays = 400;
    private const int MaxBulkRangeDays = 120;

    public static void MapOccurrenceEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/feed", FeedAsync).WithTags("Occurrences");
        v1.MapGet("/occurrences", BulkAsync).WithTags("Occurrences");
        v1.MapGet("/nodes/{id:guid}/occurrences", NodeOccurrencesAsync).WithTags("Occurrences");
    }

    /// <summary>
    /// Server-side projection of the main feed: one-time nodes dated in [from, to] or undated, and for each series
    /// today's occurrences plus the nearest next one (dates relative to each node's time zone).
    /// Clients refresh it on calendar day change even when nothing was edited.
    /// </summary>
    private static async Task<ApiResult> FeedAsync(DateOnly? from, DateOnly? to, HttpContext http, NodeAccess access,
        NodeHistoryLoader histories, IRecurrenceProjectionService projection, NodusDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = await TodayAsync(db, http.User.GetUserId(), now, ct);
        if (Range(from ?? today.AddDays(-1), to ?? today.AddDays(60), MaxRangeDays) is not { } range)
            return RangeError(MaxRangeDays);
        var (start, end) = range;

        var nodes = await access.Accessible(http.User.GetUserId()).AsNoTracking().Where(n => n.DeletedAt == null).ToListAsync(ct);
        var loaded = await histories.LoadAsync(nodes, Min(start.AddDays(-1), histories.CurrentViewFrom), ct);
        var items = nodes
            .SelectMany(n => (n.Type == NodeType.OneTime
                    ? projection.Project(loaded[n.Id], start, end, now)
                    : projection.Highlights(loaded[n.Id], now))
                .Select(o => new FeedItemDto(n.Type, n.Version, OccurrenceDto.From(o))))
            .OrderBy(i => i.Occurrence.Date is null)
            .ThenBy(i => i.Occurrence.Date)
            .ThenBy(i => i.Occurrence.Time)
            .ToList();
        return ApiResult.Ok(new { generated_at = now, from = start, to = end, items });
    }

    /// <summary>All occurrences of every accessible node in a window — the horizon for local notifications.</summary>
    private static async Task<ApiResult> BulkAsync(DateOnly? from, DateOnly? to, HttpContext http, NodeAccess access,
        NodeHistoryLoader histories, IRecurrenceProjectionService projection, NodusDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = await TodayAsync(db, http.User.GetUserId(), now, ct);
        if (Range(from ?? today.AddDays(-1), to ?? today.AddDays(45), MaxBulkRangeDays) is not { } range)
            return RangeError(MaxBulkRangeDays);
        var (start, end) = range;

        var nodes = await access.Accessible(http.User.GetUserId()).AsNoTracking().Where(n => n.DeletedAt == null).ToListAsync(ct);
        var loaded = await histories.LoadAsync(nodes, start.AddDays(-1), ct);
        var occurrences = nodes
            .SelectMany(n => projection.Project(loaded[n.Id], start, end, now))
            .Where(o => o.Date is not null)
            .OrderBy(o => o.ScheduledAt ?? DateTimeOffset.MaxValue)
            .Select(OccurrenceDto.From)
            .ToList();
        return ApiResult.Ok(new { generated_at = now, from = start, to = end, occurrences });
    }

    /// <summary>History and future of one node; past occurrences come from the revisions that governed them.</summary>
    private static async Task<ApiResult> NodeOccurrencesAsync(Guid id, DateOnly from, DateOnly to, HttpContext http,
        NodeAccess access, NodeHistoryLoader histories, IRecurrenceProjectionService projection, TimeProvider time,
        CancellationToken ct)
    {
        if (Range(from, to, MaxRangeDays) is not { } range) return RangeError(MaxRangeDays);
        var (start, end) = range;
        var node = await access.FindAsync(http.User.GetUserId(), id, ct);
        if (node is null) return ApiResult.NotFound("Node");

        var occurrences = projection.Project(await histories.LoadAsync(node, start.AddDays(-1), ct), start, end, time.GetUtcNow());
        return ApiResult.Ok(new { node_id = id, from = start, to = end, occurrences = occurrences.Select(OccurrenceDto.From) });
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    /// <summary>"Today" for default ranges: in the user's current zone (<c>PUT /v1/me/timezone</c>), UTC if unknown.</summary>
    private static async Task<DateOnly> TodayAsync(NodusDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var zoneId = await db.Users.Where(u => u.Id == userId).Select(u => u.Timezone).SingleOrDefaultAsync(ct);
        return ZonedTime.TryFindZone(zoneId, out var zone) ? ZonedTime.LocalDate(now, zone) : DateOnly.FromDateTime(now.UtcDateTime);
    }

    private static (DateOnly, DateOnly)? Range(DateOnly from, DateOnly to, int maxDays) =>
        from <= to && to.DayNumber - from.DayNumber <= maxDays ? (from, to) : null;

    private static ApiResult RangeError(int maxDays) =>
        new ValidationErrors().Add("to", $"'to' must not precede 'from' and the range must be at most {maxDays} days.").ToResult();
}
