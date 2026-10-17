using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;
using Nodus.Api.Recurrence;
using Nodus.Api.Sync;

namespace Nodus.Api.AiDrafts;

public sealed class AiOptions
{
    public const string Section = "Ai";

    public int MaxTextLength { get; init; } = 4000;
    public int MaxJobsPerDay { get; init; } = 50;

    /// <summary>LLM attempts per job (Hangfire retries included).</summary>
    public int MaxAttempts { get; init; } = 3;

    public int DraftRetentionDays { get; init; } = 14;

    /// <summary>The dictated text is erased this long after the job was created, once it is no longer processing.</summary>
    public int InputRetentionHours { get; init; } = 24;
}

public sealed record CreateDraftJobRequest(string? Text, string? Timezone, DateTimeOffset? ReferenceTime);

public sealed record ConfirmDraftJobRequest(IReadOnlyList<CreateNodeRequest>? Nodes);

public sealed record AiDraftJobDto(
    Guid Id,
    AiDraftJobStatus Status,
    string Timezone,
    DateTimeOffset ReferenceTime,
    string? Error,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<NodeDraftDto> Drafts);

public sealed record AiDraftRequestedPayload(Guid JobId);

public sealed class AiDraftService(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    NodeService nodes,
    IOptions<AiOptions> options,
    TimeProvider time)
{
    /// <summary>
    /// Stores the job and its enqueue request (outbox) in one transaction: once the client got 202 the job cannot be
    /// lost, even if the process dies before Hangfire sees it.
    /// </summary>
    public async Task<ApiResult> CreateAsync(Guid userId, CreateDraftJobRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var text = request.Text?.Trim();
        var errors = new ValidationErrors()
            .AddIf(string.IsNullOrEmpty(text), "text", "Text is required.")
            .AddIf(text?.Length > options.Value.MaxTextLength, "text", $"Text must be at most {options.Value.MaxTextLength} characters.")
            .AddIf(!ZonedTime.TryFindZone(request.Timezone, out _), "timezone", "A valid IANA time zone is required.")
            .AddIf(request.ReferenceTime is not { } at || (at - now).Duration() > TimeSpan.FromDays(2), "reference_time",
                "The moment of dictation is required and must be close to the current time.");
        if (errors.HasErrors) return errors.ToResult();

        if (await db.AiDraftJobs.CountAsync(j => j.UserId == userId && j.CreatedAt > now.AddDays(-1), ct) >= options.Value.MaxJobsPerDay)
            return ApiResult.Problem(StatusCodes.Status429TooManyRequests, "ai_quota_exceeded",
                "The daily limit of AI requests has been reached.");

        var job = new AiDraftJob
        {
            Id = Guid.NewGuid(), UserId = userId, Status = AiDraftJobStatus.Queued, InputText = text,
            Timezone = request.Timezone!, ReferenceTime = request.ReferenceTime!.Value.ToUniversalTime(),
            PromptVersion = NodeDraftParser.PromptVersion, Version = 1, CreatedAt = now, UpdatedAt = now,
            ExpiresAt = now.AddDays(options.Value.DraftRetentionDays)
        };
        db.AiDraftJobs.Add(job);
        db.OutboxEvents.Add(OutboxEvent.Create(OutboxEventTypes.AiDraftRequested,
            JsonSerializer.Serialize(new AiDraftRequestedPayload(job.Id), NodusJson.Options), now));
        Record(job);

        await unitOfWork.CommitAsync(ct);
        return ApiResult.Accepted(Dto(job, []));
    }

    public async Task<ApiResult> GetAsync(Guid userId, Guid jobId, CancellationToken ct)
    {
        var job = await db.AiDraftJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
        return job is null ? ApiResult.NotFound("Draft job") : ApiResult.Ok(await DtoAsync(job, ct));
    }

    /// <summary>The "Drafts" section: everything still waiting, independent of push delivery.</summary>
    public async Task<ApiResult> ListOpenAsync(Guid userId, CancellationToken ct)
    {
        var jobs = await db.AiDraftJobs.AsNoTracking()
            .Where(j => j.UserId == userId && (j.Status == AiDraftJobStatus.Queued || j.Status == AiDraftJobStatus.Processing ||
                                               j.Status == AiDraftJobStatus.Ready))
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);
        return ApiResult.Ok(new { jobs = await DtosAsync(jobs, ct) });
    }

    /// <summary>
    /// Confirms the reviewed drafts. With <c>nodes</c> the nodes are created and the job confirmed in one transaction;
    /// without, the client has already created them through <c>POST /v1/nodes/batch</c>.
    /// </summary>
    public async Task<ApiResult> ConfirmAsync(Guid userId, Guid jobId, ConfirmDraftJobRequest? request, CancellationToken ct)
    {
        var job = await db.AiDraftJobs.SingleOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
        if (job is null) return ApiResult.NotFound("Draft job");
        if (job.Status == AiDraftJobStatus.Confirmed) return ApiResult.Ok(await DtoAsync(job, ct));
        if (job.Status != AiDraftJobStatus.Ready)
            return ApiResult.Problem(StatusCodes.Status409Conflict, "job_not_ready", $"The job is {job.Status.ToString().ToLowerInvariant()}.");

        object? created = null;
        if (request?.Nodes is { Count: > 0 } items)
        {
            var batch = await nodes.CreateBatchAsync(userId, new BatchCreateNodesRequest(items), ct);
            if (!batch.IsSuccess) return batch;
            created = batch.Value;
        }

        var now = time.GetUtcNow();
        job.Status = AiDraftJobStatus.Confirmed;
        job.ConfirmedAt = now;
        job.UpdatedAt = now;
        job.Version++;
        Record(job);
        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(new { job = await DtoAsync(job, ct), created });
    }

    public async Task<ApiResult> CancelAsync(Guid userId, Guid jobId, CancellationToken ct)
    {
        var job = await db.AiDraftJobs.SingleOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
        if (job is null) return ApiResult.NotFound("Draft job");
        if (!job.IsOpen) return ApiResult.Ok(await DtoAsync(job, ct));

        job.Status = AiDraftJobStatus.Cancelled;
        job.UpdatedAt = time.GetUtcNow();
        job.Version++;
        Record(job);
        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(await DtoAsync(job, ct));
    }

    public void Record(AiDraftJob job) =>
        unitOfWork.Journal.Record(SyncEntityType.AiDraftJob, job.Id.ToString(), job.Version, SyncOperation.Upsert, [job.UserId]);

    public async Task<AiDraftJobDto> DtoAsync(AiDraftJob job, CancellationToken ct) => (await DtosAsync([job], ct))[0];

    public async Task<IReadOnlyList<AiDraftJobDto>> DtosAsync(IReadOnlyCollection<AiDraftJob> jobs, CancellationToken ct)
    {
        var ids = jobs.Select(j => j.Id).ToArray();
        var drafts = (await db.AiNodeDrafts.AsNoTracking().Where(d => ids.Contains(d.JobId)).ToListAsync(ct))
            .ToLookup(d => d.JobId);
        return jobs.Select(j => Dto(j, drafts[j.Id].OrderBy(d => d.Index)
            .Select(d => JsonSerializer.Deserialize<NodeDraftDto>(d.Payload, NodusJson.Options)!).ToList())).ToList();
    }

    private static AiDraftJobDto Dto(AiDraftJob job, IReadOnlyList<NodeDraftDto> drafts) =>
        new(job.Id, job.Status, job.Timezone, job.ReferenceTime, job.Status == AiDraftJobStatus.Failed ? job.LastError : null,
            job.Version, job.CreatedAt, job.ReadyAt, job.ConfirmedAt, job.ExpiresAt,
            job.Status is AiDraftJobStatus.Ready or AiDraftJobStatus.Confirmed ? drafts : []);
}
