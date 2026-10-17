using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Hangfire;
using Nodus.Api.Infrastructure.Llm;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Notifications;
using Nodus.Api.Sync;

namespace Nodus.Api.AiDrafts;

public sealed class AiDraftRequestedOutboxHandler(IBackgroundJobs jobs) : IOutboxHandler
{
    public string EventType => OutboxEventTypes.AiDraftRequested;

    public Task HandleAsync(OutboxEvent outboxEvent, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<AiDraftRequestedPayload>(outboxEvent.Payload, NodusJson.Options)!;
        return jobs.EnqueueAsync<AiDraftRunner>(r => r.RunAsync(payload.JobId, CancellationToken.None), HangfireQueues.AiDrafts);
    }
}

/// <summary>
/// Processes one draft job. Idempotent: a duplicate enqueue or a retry after a crash finds the job no longer queued or
/// replaces its drafts, so retries and provider fallback never duplicate drafts. A job cancelled while the model was
/// running keeps its cancelled state.
/// </summary>
public sealed class AiDraftRunner(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    AiDraftService drafts,
    ILlmClient llm,
    PushNotifier push,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<AiDraftRunner> logger)
{
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [15, 60])]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.AiDraftJobs.SingleOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status is not (AiDraftJobStatus.Queued or AiDraftJobStatus.Processing)) return;
        if (job.InputText is null)
        {
            await FailAsync(job, "input_expired", ct);
            return;
        }

        job.Status = AiDraftJobStatus.Processing;
        job.Attempts++;
        Touch(job);
        await unitOfWork.CommitAsync(ct);

        IReadOnlyList<NodeDraftDto> parsed;
        LlmResult result;
        try
        {
            result = await llm.GenerateAsync(NodeDraftParser.BuildRequest(job.InputText, job.Timezone, job.ReferenceTime), ct);
            parsed = NodeDraftParser.Parse(result.Content, job.Timezone, job.ReferenceTime, result.Provider);
        }
        catch (LlmException e) when (e.IsTransient && job.Attempts < options.Value.MaxAttempts)
        {
            logger.LogWarning("Draft job {JobId} attempt {Attempt} failed: {Category}", job.Id, job.Attempts, e.Category);
            job.LastError = e.Category.ToString();
            await db.SaveChangesAsync(ct);
            throw; // retried by Hangfire
        }
        catch (LlmException e)
        {
            logger.LogError("Draft job {JobId} failed: {Category}", job.Id, e.Category);
            await FailAsync(job, ErrorCode(e.Category), ct);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var current = await db.AiDraftJobs.FromSql($"SELECT * FROM ai_draft_jobs WHERE id = {jobId} FOR UPDATE").SingleAsync(ct);
        if (current.Status != AiDraftJobStatus.Processing) return; // cancelled meanwhile

        if (parsed.Count == 0)
        {
            await FailAsync(current, "nothing_recognized", ct);
            await transaction.CommitAsync(ct);
            unitOfWork.AfterCommit();
            return;
        }

        await db.AiNodeDrafts.Where(d => d.JobId == jobId).ExecuteDeleteAsync(ct);
        db.AiNodeDrafts.AddRange(parsed.Select(d => new AiNodeDraft
        {
            JobId = jobId, Index = d.Index, Payload = JsonSerializer.Serialize(d, NodusJson.Options)
        }));
        current.Status = AiDraftJobStatus.Ready;
        current.ReadyAt = time.GetUtcNow();
        current.Provider = result.Provider;
        current.Model = result.Model;
        current.LastError = null;
        Touch(current);
        // The push carries the job id and a neutral text only: never the drafts or the dictated speech.
        push.Request(new PushRequest(current.UserId, $"ai-drafts-{current.Id}", PushKinds.AiDraftsReady,
            "Черновики готовы", "Проверьте и подтвердите новые узлы.",
            new Dictionary<string, string> { ["job_id"] = current.Id.ToString() }, CollapseId: $"ai-drafts-{current.Id}"));
        await unitOfWork.CommitAsync(ct);
        await transaction.CommitAsync(ct);
        unitOfWork.AfterCommit();
    }

    private async Task FailAsync(AiDraftJob job, string error, CancellationToken ct)
    {
        job.Status = AiDraftJobStatus.Failed;
        job.LastError = error;
        Touch(job);
        push.Request(new PushRequest(job.UserId, $"ai-drafts-{job.Id}", PushKinds.AiDraftsReady,
            "Не получилось разобрать запись", "Откройте раздел «Черновики», чтобы попробовать ещё раз.",
            new Dictionary<string, string> { ["job_id"] = job.Id.ToString() }, CollapseId: $"ai-drafts-{job.Id}"));
        await unitOfWork.CommitAsync(ct);
    }

    private void Touch(AiDraftJob job)
    {
        job.UpdatedAt = time.GetUtcNow();
        job.Version++;
        drafts.Record(job);
    }

    private static string ErrorCode(LlmErrorCategory category) => category switch
    {
        LlmErrorCategory.NotConfigured or LlmErrorCategory.Unauthorized or LlmErrorCategory.BadRequest => "ai_unavailable",
        LlmErrorCategory.InvalidResponse => "invalid_ai_response",
        _ => "ai_temporarily_unavailable"
    };
}
