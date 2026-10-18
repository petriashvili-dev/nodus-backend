using Microsoft.EntityFrameworkCore;
using Nodus.Api.Infrastructure.Observability;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Sync;

namespace Nodus.Api.Infrastructure.Llm;

/// <summary>Fields every AI-backed job shares; statuses differ per job type.</summary>
public interface IAiJob
{
    Guid Id { get; }
    Guid UserId { get; }
    string? InputText { get; set; }
    int Attempts { get; set; }
    string? LastError { get; set; }
}

/// <summary>
/// Shared lifecycle of AI-backed Hangfire jobs (voice drafts, imports):
/// <list type="number">
/// <item>exit unless the job is still pending — duplicate enqueues and retries after a crash are harmless;</item>
/// <item>mark processing and count the attempt before the slow call;</item>
/// <item>call the model; transient failures with attempts left are rethrown for Hangfire to retry, everything else
/// fails the job with an error code;</item>
/// <item>re-read the job under <c>FOR UPDATE</c> and drop the result if it was cancelled meanwhile;</item>
/// <item>store the result (or a domain failure such as "nothing recognised") in one transaction.</item>
/// </list>
/// Outcomes are counted in <c>nodus.ai.jobs</c>.
/// </summary>
public abstract class AiJobRunner<TJob, TOutput>(NodusDbContext db, UnitOfWork unitOfWork, ILlmClient llm, ILogger logger)
    where TJob : class, IAiJob
{
    protected NodusDbContext Db { get; } = db;

    /// <summary>Metrics tag and log prefix, e.g. <c>drafts</c>.</summary>
    protected abstract string Kind { get; }

    /// <summary>Table of <typeparamref name="TJob"/> for the row lock (a code constant, never input).</summary>
    protected abstract string Table { get; }

    protected abstract int MaxAttempts { get; }

    protected abstract bool IsPending(TJob job);

    protected abstract bool IsProcessing(TJob job);

    /// <summary>Sets the processing status and journals the change.</summary>
    protected abstract void MarkProcessing(TJob job);

    protected abstract LlmRequest BuildRequest(TJob job, string input);

    /// <exception cref="LlmException">The output cannot be used (treated as a transient invalid response).</exception>
    protected abstract TOutput Parse(TJob job, LlmResult result);

    /// <summary>Stores the result on the locked job; returns an error code to fail with instead, or null.</summary>
    protected abstract Task<string?> CompleteAsync(TJob job, TOutput output, LlmResult result, CancellationToken ct);

    /// <summary>Sets the failed status and error, journals, requests the failure push. No commit.</summary>
    protected abstract void Fail(TJob job, string error);

    protected async Task ExecuteAsync(Guid jobId, CancellationToken ct)
    {
        var job = await Db.Set<TJob>().SingleOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || !IsPending(job)) return;
        if (job.InputText is not { } input)
        {
            await FailAndCommitAsync(job, "input_expired", ct);
            return;
        }

        job.Attempts++;
        MarkProcessing(job);
        await unitOfWork.CommitAsync(ct);

        TOutput output;
        LlmResult result;
        try
        {
            result = await llm.GenerateAsync(BuildRequest(job, input), ct);
            output = Parse(job, result);
        }
        catch (LlmException e) when (e.IsTransient && job.Attempts < MaxAttempts)
        {
            logger.LogWarning("AI job {Kind} {JobId} attempt {Attempt} failed: {Category}", Kind, job.Id, job.Attempts, e.Category);
            NodusMetrics.AiJob(Kind, "retry");
            job.LastError = e.Category.ToString();
            await Db.SaveChangesAsync(ct);
            throw; // retried by Hangfire
        }
        catch (LlmException e)
        {
            logger.LogError("AI job {Kind} {JobId} failed: {Category}", Kind, job.Id, e.Category);
            await FailAndCommitAsync(job, ErrorCode(e.Category), ct);
            return;
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(ct);
        Db.ChangeTracker.Clear();
        var current = await Db.Set<TJob>()
            .FromSqlRaw($"SELECT * FROM {Table} WHERE id = {{0}} FOR UPDATE", jobId)
            .SingleAsync(ct);
        if (!IsProcessing(current))
        {
            NodusMetrics.AiJob(Kind, "discarded"); // cancelled while the model was running
            return;
        }

        var error = await CompleteAsync(current, output, result, ct);
        if (error is not null) Fail(current, error);
        await unitOfWork.CommitAsync(ct);
        await transaction.CommitAsync(ct);
        unitOfWork.AfterCommit();
        NodusMetrics.AiJob(Kind, error ?? "ready");
    }

    private async Task FailAndCommitAsync(TJob job, string error, CancellationToken ct)
    {
        Fail(job, error);
        await unitOfWork.CommitAsync(ct);
        NodusMetrics.AiJob(Kind, error);
    }

    protected static string ErrorCode(LlmErrorCategory category) => category switch
    {
        LlmErrorCategory.NotConfigured or LlmErrorCategory.Unauthorized or LlmErrorCategory.BadRequest => "ai_unavailable",
        LlmErrorCategory.InvalidResponse => "invalid_ai_response",
        _ => "ai_temporarily_unavailable"
    };
}
