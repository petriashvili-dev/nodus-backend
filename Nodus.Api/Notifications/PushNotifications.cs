using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nodus.Api.Common;
using Nodus.Api.Devices;
using Nodus.Api.Infrastructure.Apns;
using Nodus.Api.Infrastructure.Hangfire;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Api.Notifications;

/// <summary>
/// A push to every installation of a user. <see cref="EventId"/> lets the client de-duplicate pushes against
/// SSE/in-app notifications; the data must not contain node content.
/// </summary>
public sealed record PushRequest(
    Guid UserId,
    string EventId,
    string Kind,
    string Title,
    string Body,
    Dictionary<string, string> Data,
    string? CollapseId = null);

public static class PushKinds
{
    public const string AiDraftsReady = "ai_drafts_ready";
    public const string SharedNodeChanged = "shared_node_changed";
    public const string ConnectionChanged = "connection_changed";
}

/// <summary>
/// Requests pushes from inside a unit of work: the request is stored as an outbox event in the same transaction,
/// so a push is sent only for committed data and is never lost between commit and enqueue.
/// APNs is not a delivery guarantee; everything a push announces is also available through sync.
/// </summary>
public sealed class PushNotifier(NodusDbContext db, TimeProvider time)
{
    public void Request(PushRequest request) =>
        db.OutboxEvents.Add(OutboxEvent.Create(OutboxEventTypes.PushRequested,
            JsonSerializer.Serialize(request, NodusJson.Options), time.GetUtcNow()));
}

public sealed class PushRequestedOutboxHandler(IBackgroundJobs jobs) : IOutboxHandler
{
    public string EventType => OutboxEventTypes.PushRequested;

    public Task HandleAsync(OutboxEvent outboxEvent, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<PushRequest>(outboxEvent.Payload, NodusJson.Options)!;
        return jobs.EnqueueAsync<PushDeliveryJob>(job => job.SendAsync(request, CancellationToken.None), HangfireQueues.Notifications);
    }
}

public sealed class PushDeliveryJob(NodusDbContext db, IApnsClient apns, TimeProvider time, ILogger<PushDeliveryJob> logger)
{
    /// <summary>
    /// Sends to all installations with a token; dead tokens are cleared. Throws (so Hangfire retries) only when nothing
    /// was delivered and a failure was transient, which keeps duplicate pushes on retry to a minimum.
    /// </summary>
    [global::Hangfire.AutomaticRetry(Attempts = 5)]
    public async Task SendAsync(PushRequest request, CancellationToken ct)
    {
        var devices = await db.Devices.Where(d => d.UserId == request.UserId && d.ApnsToken != null).ToListAsync(ct);
        var alert = new ApnsAlert(request.Title, request.Body,
            new Dictionary<string, string>(request.Data) { ["event_id"] = request.EventId, ["kind"] = request.Kind },
            request.CollapseId, request.Kind);

        var sent = 0;
        var transient = 0;
        foreach (var device in devices)
        {
            var result = await apns.SendAsync(device.ApnsToken!, device.ApnsEnvironment, alert, ct);
            switch (result.Outcome)
            {
                case ApnsOutcome.Sent:
                    sent++;
                    if (device.ApnsEnvironment == ApnsEnvironment.Unknown && result.Environment is { } learned)
                        device.ApnsEnvironment = learned;
                    break;
                case ApnsOutcome.InvalidToken:
                    device.ApnsToken = null;
                    device.TokenInvalidatedAt = time.GetUtcNow();
                    break;
                case ApnsOutcome.Retryable:
                    transient++;
                    break;
                default:
                    logger.LogWarning("Push {EventId} to device {DeviceId} failed: {Reason}", request.EventId, device.Id, result.Reason);
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        if (sent == 0 && transient > 0)
            throw new InvalidOperationException($"Push {request.EventId} was not delivered because of transient APNs failures.");
    }
}
