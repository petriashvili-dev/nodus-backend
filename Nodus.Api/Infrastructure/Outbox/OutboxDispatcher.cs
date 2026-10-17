using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.Infrastructure.Observability;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Api.Infrastructure.Outbox;

public interface IOutboxHandler
{
    string EventType { get; }

    /// <summary>Must be quick and idempotent (in-memory fanout, Hangfire enqueue); slow work belongs in jobs.</summary>
    Task HandleAsync(OutboxEvent outboxEvent, CancellationToken ct);
}

/// <summary>Wakes the dispatcher right after a commit; polling covers signals lost to crashes or restarts.</summary>
public sealed class OutboxSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    public int PollSeconds { get; init; } = 5;
    public int BatchSize { get; init; } = 100;
    public int MaxAttempts { get; init; } = 10;

    /// <summary>Off in tests, which dispatch explicitly for deterministic ordering.</summary>
    public bool RunInBackground { get; init; } = true;
}

public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    OutboxSignal signal,
    TimeProvider time,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunInBackground) return;
        var poll = TimeSpan.FromSeconds(options.Value.PollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await DispatchBatchAsync(stoppingToken) == options.Value.BatchSize) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Outbox dispatch failed");
            }

            try
            {
                await signal.WaitAsync(poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Processes one batch; rows are locked with SKIP LOCKED so a future second worker cannot double-process.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodusDbContext>();
        var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().ToDictionary(h => h.EventType);
        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.OutboxEvents
            .FromSql($"""
                SELECT * FROM outbox_events
                WHERE processed_at IS NULL AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                ORDER BY id
                LIMIT {options.Value.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        foreach (var outboxEvent in batch)
        {
            try
            {
                if (!handlers.TryGetValue(outboxEvent.Type, out var handler))
                    throw new InvalidOperationException($"No outbox handler for '{outboxEvent.Type}'.");
                await handler.HandleAsync(outboxEvent, ct);
                outboxEvent.ProcessedAt = now;
                NodusMetrics.OutboxDispatched(outboxEvent.Type, "processed", now - outboxEvent.CreatedAt);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                outboxEvent.Attempts++;
                outboxEvent.LastError = e.GetType().Name;
                NodusMetrics.OutboxDispatched(outboxEvent.Type,
                    outboxEvent.Attempts >= options.Value.MaxAttempts ? "abandoned" : "failed", TimeSpan.Zero);
                if (outboxEvent.Attempts >= options.Value.MaxAttempts)
                {
                    outboxEvent.ProcessedAt = outboxEvent.FailedAt = now;
                    logger.LogError(e, "Outbox event {EventId} ({Type}) abandoned", outboxEvent.Id, outboxEvent.Type);
                }
                else
                {
                    var backoff = TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, outboxEvent.Attempts)) * (0.8 + Random.Shared.NextDouble() * 0.4));
                    outboxEvent.NextAttemptAt = now + backoff;
                    logger.LogWarning(e, "Outbox event {EventId} ({Type}) failed, attempt {Attempt}", outboxEvent.Id,
                        outboxEvent.Type, outboxEvent.Attempts);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return batch.Count;
    }
}
