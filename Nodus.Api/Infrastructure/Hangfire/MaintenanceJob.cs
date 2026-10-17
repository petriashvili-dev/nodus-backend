using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.Infrastructure.Persistence;

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
public sealed class MaintenanceJob(NodusDbContext db, IOptions<RetentionOptions> options, TimeProvider time,
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

        logger.LogInformation(
            "Retention: {Operations} idempotency records, sync changes for {Users} users, {Outbox} outbox events, " +
            "{Codes} email codes, {Tokens} refresh tokens removed", operations, changes, outbox, codes, tokens);
    }
}
