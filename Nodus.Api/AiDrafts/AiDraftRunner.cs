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
/// Turns dictated text into drafts (lifecycle in <see cref="AiJobRunner{TJob,TOutput}"/>). Retries replace the job's
/// drafts instead of adding to them, so retries and provider fallback never duplicate drafts.
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
    : AiJobRunner<AiDraftJob, IReadOnlyList<NodeDraftDto>>(db, unitOfWork, llm, logger)
{
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [15, 60])]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public Task RunAsync(Guid jobId, CancellationToken ct) => ExecuteAsync(jobId, ct);

    protected override string Kind => "drafts";
    protected override string Table => "ai_draft_jobs";
    protected override int MaxAttempts => options.Value.MaxAttempts;

    protected override bool IsPending(AiDraftJob job) => job.Status is AiDraftJobStatus.Queued or AiDraftJobStatus.Processing;
    protected override bool IsProcessing(AiDraftJob job) => job.Status == AiDraftJobStatus.Processing;

    protected override void MarkProcessing(AiDraftJob job)
    {
        job.Status = AiDraftJobStatus.Processing;
        Touch(job);
    }

    protected override LlmRequest BuildRequest(AiDraftJob job, string input) =>
        NodeDraftParser.BuildRequest(input, job.Timezone, job.ReferenceTime);

    protected override IReadOnlyList<NodeDraftDto> Parse(AiDraftJob job, LlmResult result) =>
        NodeDraftParser.Parse(result.Content, job.Timezone, job.ReferenceTime, result.Provider);

    protected override async Task<string?> CompleteAsync(AiDraftJob job, IReadOnlyList<NodeDraftDto> parsed, LlmResult result,
        CancellationToken ct)
    {
        if (parsed.Count == 0) return "nothing_recognized";

        await Db.AiNodeDrafts.Where(d => d.JobId == job.Id).ExecuteDeleteAsync(ct);
        Db.AiNodeDrafts.AddRange(parsed.Select(d => new AiNodeDraft
        {
            JobId = job.Id, Index = d.Index, Payload = JsonSerializer.Serialize(d, NodusJson.Options)
        }));
        job.Status = AiDraftJobStatus.Ready;
        job.ReadyAt = time.GetUtcNow();
        job.Provider = result.Provider;
        job.Model = result.Model;
        job.LastError = null;
        Touch(job);
        // The push carries the job id and a neutral text only: never the drafts or the dictated speech.
        push.Request(new PushRequest(job.UserId, $"ai-drafts-{job.Id}", PushKinds.AiDraftsReady,
            "Nodus распутал ваши мысли", "Проверьте и подтвердите новые узлы.",
            new Dictionary<string, string> { ["job_id"] = job.Id.ToString() }, CollapseId: $"ai-drafts-{job.Id}"));
        return null;
    }

    protected override void Fail(AiDraftJob job, string error)
    {
        job.Status = AiDraftJobStatus.Failed;
        job.LastError = error;
        Touch(job);
        push.Request(new PushRequest(job.UserId, $"ai-drafts-{job.Id}", PushKinds.AiDraftsReady,
            "Не получилось разобрать запись", "Откройте раздел «Черновики», чтобы попробовать ещё раз.",
            new Dictionary<string, string> { ["job_id"] = job.Id.ToString() }, CollapseId: $"ai-drafts-{job.Id}"));
    }

    private void Touch(AiDraftJob job)
    {
        job.UpdatedAt = time.GetUtcNow();
        job.Version++;
        drafts.Record(job);
    }
}
