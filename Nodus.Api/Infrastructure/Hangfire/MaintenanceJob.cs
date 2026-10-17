using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.AiDrafts;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Sync;

namespace Nodus.Api.Infrastructure.Hangfire;

public sealed class RetentionOptions
{
    public const string Section = "Retention";

    /// <summary>Must exceed the longest time a client outbox may stay offline and keep retrying.</summary>
    public int IdempotencyDays { get; init; } = 30;

    /// <summary>Older cursors get 410 and a full resync.</summary>
    public int SyncChangeDays { get; init; } = 90;

    public int ProcessedOutboxDays { get; init; } = 7;
    public int EmailCodeDays { get; init; } = 7;
}

/// <summary>Daily retention cleanup. Correctness never depends on it running on time.</summary>
public sealed class MaintenanceJob(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    AiDraftService aiDrafts,
    IOptions<RetentionOptions> options,
    IOptions<AiOptions> aiOptions,
    TimeProvider time,
    ILogger<MaintenanceJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var o = options.Value;

        var operations = await db.ClientOperations.Where(x => x.CreatedAt < now.AddDays(-o.IdempotencyDays)).ExecuteDeleteAsync(ct);

        // Pruning and moving each user's pruned watermark happen in one statement.
        var syncCutoff = now.AddDays(-o.SyncChangeDays);
        var changes = await db.Database.ExecuteSqlAsync($"""
            WITH pruned AS (
                DELETE FROM sync_changes WHERE created_at < {syncCutoff}
                RETURNING recipient_user_id, revision)
            UPDATE users u SET sync_pruned_revision = GREATEST(u.sync_pruned_revision, p.max_revision)
            FROM (SELECT recipient_user_id, MAX(revision) AS max_revision FROM pruned GROUP BY recipient_user_id) p
            WHERE u.id = p.recipient_user_id
            """, ct);

        var outbox = await db.OutboxEvents
            .Where(x => x.ProcessedAt != null && x.ProcessedAt < now.AddDays(-o.ProcessedOutboxDays)).ExecuteDeleteAsync(ct);
        var codes = await db.EmailVerificationCodes.Where(x => x.ExpiresAt < now.AddDays(-o.EmailCodeDays)).ExecuteDeleteAsync(ct);
        var tokens = await db.RefreshTokens.Where(x => x.ExpiresAt < now).ExecuteDeleteAsync(ct);
        var ai = await AiRetentionAsync(now, ct);

        logger.LogInformation(
            "Retention: {Operations} idempotency records, sync changes for {Users} users, {Outbox} outbox events, " +
            "{Codes} email codes, {Tokens} refresh tokens removed; {AiJobs} AI jobs expired or timed out",
            operations, changes, outbox, codes, tokens, ai);
    }

    /// <summary>
    /// Dictated text is erased after <see cref="AiOptions.InputRetentionHours"/>; unconfirmed drafts expire; jobs stuck in
    /// processing (lost worker) fail so the client stops waiting. Status changes are journaled for sync.
    /// </summary>
    private async Task<int> AiRetentionAsync(DateTimeOffset now, CancellationToken ct)
    {
        var o = aiOptions.Value;
        await db.AiDraftJobs
            .Where(j => j.InputText != null && j.CreatedAt < now.AddHours(-o.InputRetentionHours) &&
                        j.Status != AiDraftJobStatus.Queued && j.Status != AiDraftJobStatus.Processing)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.InputText, (string?)null).SetProperty(j => j.InputPurgedAt, now), ct);
        await db.AiNodeDrafts
            .Where(d => db.AiDraftJobs.Any(j => j.Id == d.JobId && j.Status != AiDraftJobStatus.Ready &&
                                                j.UpdatedAt < now.AddDays(-o.DraftRetentionDays)))
            .ExecuteDeleteAsync(ct);

        var jobs = await db.AiDraftJobs
            .Where(j => (j.Status == AiDraftJobStatus.Ready && j.ExpiresAt < now) ||
                        ((j.Status == AiDraftJobStatus.Queued || j.Status == AiDraftJobStatus.Processing) &&
                         j.UpdatedAt < now.AddHours(-2)))
            .Take(500)
            .ToListAsync(ct);
        foreach (var job in jobs)
        {
            if (job.Status == AiDraftJobStatus.Ready) job.Status = AiDraftJobStatus.Expired;
            else (job.Status, job.LastError) = (AiDraftJobStatus.Failed, "timed_out");
            job.UpdatedAt = now;
            job.Version++;
            aiDrafts.Record(job);
        }

        await unitOfWork.CommitAsync(ct);
        return jobs.Count;
    }
}
