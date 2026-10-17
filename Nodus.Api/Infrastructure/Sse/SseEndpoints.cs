using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.Authentication;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Api.Infrastructure.Sse;

public sealed class SseOptions
{
    public const string Section = "Sse";

    public int HeartbeatSeconds { get; init; } = 20;

    /// <summary>Reconnect delay suggested to clients (<c>retry:</c>); clients add their own backoff on top.</summary>
    public int RetryMilliseconds { get; init; } = 3000;
}

public static class SseEndpoints
{
    public static void MapSseEndpoints(this RouteGroupBuilder v1) =>
        v1.MapGet("/events", StreamAsync).WithTags("Sync").ExcludeFromDescription();

    /// <summary>
    /// <c>text/event-stream</c> of <c>sync.required</c> invalidations; the event id is the user's sync revision.
    /// On connect the server compares <c>Last-Event-ID</c> (or <c>last_event_id</c>) with the current revision and
    /// signals immediately if the client is behind. Clients must still run Delta Sync after every reconnect:
    /// SSE is not a delivery guarantee.
    /// </summary>
    private static async Task StreamAsync(HttpContext http, NodusDbContext db, IRealtimeHub hub, IOptions<SseOptions> options,
        IHostApplicationLifetime lifetime, CancellationToken requestAborted)
    {
        var userId = http.User.GetUserId();
        using var subscription = hub.Subscribe(userId);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, lifetime.ApplicationStopping);
        var ct = stop.Token;

        var lastEventId = ParseRevision(http.Request.Headers["Last-Event-ID"].ToString())
                          ?? ParseRevision(http.Request.Query["last_event_id"].ToString());
        var current = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.SyncRevision).SingleOrDefaultAsync(ct);

        var response = http.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering (nginx)
        await response.WriteAsync($"retry: {options.Value.RetryMilliseconds}\n\n", ct);
        if (lastEventId is null || current > lastEventId) await WriteSignalAsync(response, current, ct);
        else await response.Body.FlushAsync(ct);

        var heartbeat = TimeSpan.FromSeconds(options.Value.HeartbeatSeconds);
        // Signals at or below what the client already knows (or was just told) are redundant.
        var sent = Math.Max(lastEventId ?? 0, current);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(heartbeat);
                try
                {
                    var signal = await subscription.Signals.ReadAsync(wait.Token);
                    if (signal.Revision <= sent) continue;
                    sent = signal.Revision;
                    await WriteSignalAsync(response, signal.Revision, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await response.WriteAsync(": ping\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or the server is shutting down; the client reconnects and catches up via sync.
        }
    }

    private static async Task WriteSignalAsync(HttpResponse response, long revision, CancellationToken ct)
    {
        var id = revision.ToString(CultureInfo.InvariantCulture);
        await response.WriteAsync($"id: {id}\nevent: sync.required\ndata: {{\"revision\":{id},\"event_id\":\"{id}\"}}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static long? ParseRevision(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ? revision : null;
}
