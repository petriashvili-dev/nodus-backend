using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.Common;
using Nodus.Api.Common.Resolvers;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;
using Nodus.Api.Occurrences;
using Nodus.Api.Recurrence;
using Nodus.Api.Sync;

namespace Nodus.Api.DataPortability;

public sealed class ImportOptions
{
    public const string Section = "Import";

    public int MaxContentChars { get; init; } = 200_000;

    /// <summary>Foreign files above this size are not sent to the model.</summary>
    public int MaxAiInputChars { get; init; } = 30_000;

    public int MaxNodes { get; init; } = 1000;
    public int MaxAiImportsPerDay { get; init; } = 5;
    public int MaxAttempts { get; init; } = 3;
    public int PreviewRetentionDays { get; init; } = 7;
}

public sealed record ImportRequest(string? Content, string? FileName, string? Timezone);

public sealed record ConfirmImportRequest(IReadOnlyList<int>? Indexes);

public sealed record ImportResult(
    int CreatedNodes,
    int SkippedDuplicates,
    int SkippedInactive,
    IReadOnlyList<ImportIssue> Issues);

public sealed record ImportPreviewNode(
    int Index,
    NodeType Type,
    string? Title,
    string? Description,
    string? Timezone,
    DateOnly? Date,
    TimeOnly? Time,
    RecurrenceRule? Recurrence,
    bool? TrackYears);

public sealed record ImportJobDto(
    Guid Id,
    ImportJobStatus Status,
    string? FileName,
    string? Error,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<ImportPreviewNode> Preview,
    IReadOnlyList<ImportIssue> PreviewIssues,
    ImportResult? Result);

public sealed record ImportRequestedPayload(Guid JobId);

/// <summary>
/// Import never replaces anything: it only adds nodes. Inactive entries (past or done one-time nodes, ended series)
/// and nodes identical to the user's existing active nodes or to earlier entries of the same file are skipped,
/// so importing the same file twice adds nothing the second time.
/// </summary>
public sealed class ImportService(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    NodeService nodes,
    ISelectionResolver<ImportFormat, IImportFormatParser> parsers,
    IOptions<ImportOptions> options,
    TimeProvider time)
{
    /// <summary>
    /// A file with a known <c>(source, version)</c> is parsed by its parser and imported at once. An unknown Nodus
    /// version is rejected (it comes from a newer app). Anything else is converted by the model into the current
    /// format and returned as a preview to confirm.
    /// </summary>
    public async Task<ApiResult> ImportAsync(Guid userId, ImportRequest request, CancellationToken ct)
    {
        var o = options.Value;
        var content = request.Content?.Trim();
        if (string.IsNullOrEmpty(content))
            return new ValidationErrors().Add("content", "The file content is required.").ToResult();
        if (content.Length > o.MaxContentChars)
            return new ValidationErrors().Add("content", $"The file must be at most {o.MaxContentChars} characters.").ToResult();

        var defaults = await DefaultsAsync(userId, request.Timezone, ct);
        if (TryReadFormat(content, out var format, out var document))
        {
            if (parsers.GetOrDefault(format) is { } parser)
            {
                var result = await ApplyAsync(userId, parser.Parse(document, defaults), null, ct);
                await unitOfWork.CommitAsync(ct);
                return ApiResult.Ok(new { mode = "direct", format = format.ToString(), result });
            }

            if (format.Source == ImportFormat.NodusSource)
                return ApiResult.Problem(StatusCodes.Status400BadRequest, "unsupported_format_version",
                    $"This file was created by a newer version of Nodus (format {format}); update the app to import it.");
        }

        return await QueueAiImportAsync(userId, content, request.FileName, defaults.Timezone, ct);
    }

    public async Task<ApiResult> GetAsync(Guid userId, Guid jobId, CancellationToken ct) =>
        await db.ImportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct) is { } job
            ? ApiResult.Ok(Dto(job))
            : ApiResult.NotFound("Import");

    /// <summary>Applies the confirmed preview (all entries, or only <c>indexes</c>).</summary>
    public async Task<ApiResult> ConfirmAsync(Guid userId, Guid jobId, ConfirmImportRequest? request, CancellationToken ct)
    {
        var job = await db.ImportJobs.SingleOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
        if (job is null) return ApiResult.NotFound("Import");
        if (job.Status == ImportJobStatus.Completed) return ApiResult.Ok(Dto(job));
        if (job.Status != ImportJobStatus.Ready || job.Preview is null)
            return ApiResult.Problem(StatusCodes.Status409Conflict, "import_not_ready",
                $"The import is {job.Status.ToString().ToLowerInvariant()}.");

        var parsed = ParsePreview(job);
        var selected = request?.Indexes is { Count: > 0 } indexes ? indexes.ToHashSet() : null;
        var result = await ApplyAsync(userId, parsed, selected, ct);

        var now = time.GetUtcNow();
        job.Status = ImportJobStatus.Completed;
        job.Result = JsonSerializer.Serialize(result, NodusJson.Options);
        job.CompletedAt = now;
        Touch(job, now);
        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(Dto(job));
    }

    public async Task<ApiResult> CancelAsync(Guid userId, Guid jobId, CancellationToken ct)
    {
        var job = await db.ImportJobs.SingleOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
        if (job is null) return ApiResult.NotFound("Import");
        if (job.Status is ImportJobStatus.Queued or ImportJobStatus.Processing or ImportJobStatus.Ready)
        {
            job.Status = ImportJobStatus.Cancelled;
            job.InputText = null;
            job.Preview = null;
            Touch(job, time.GetUtcNow());
            await unitOfWork.CommitAsync(ct);
        }

        return ApiResult.Ok(Dto(job));
    }

    public IImportFormatParser CurrentParser =>
        parsers.GetOrDefault(ImportFormat.Current)
        ?? throw new InvalidOperationException($"No parser is registered for the current format {ImportFormat.Current}.");

    public ImportParseResult ParsePreview(ImportJob job)
    {
        using var document = JsonDocument.Parse(job.Preview!);
        return CurrentParser.Parse(document.RootElement, new ImportDefaults(job.Timezone, time.GetUtcNow()));
    }

    public void Touch(ImportJob job, DateTimeOffset now)
    {
        job.UpdatedAt = now;
        job.Version++;
        unitOfWork.Journal.Record(SyncEntityType.ImportJob, job.Id.ToString(), job.Version, SyncOperation.Upsert, [job.UserId]);
    }

    public ImportJobDto Dto(ImportJob job)
    {
        var preview = job is { Status: ImportJobStatus.Ready, Preview: not null } ? ParsePreview(job) : null;
        return new ImportJobDto(job.Id, job.Status, job.FileName, job.Status == ImportJobStatus.Failed ? job.LastError : null,
            job.Version, job.CreatedAt, job.CompletedAt, job.ExpiresAt,
            preview?.Candidates.Select(c => new ImportPreviewNode(c.Index, c.Type, c.Input.Title, c.Input.Description,
                c.Input.Timezone, c.Input.Date, c.Input.Time, c.Input.Recurrence, c.Input.TrackYears)).ToList() ?? [],
            preview?.Issues ?? [],
            job.Result is null ? null : JsonSerializer.Deserialize<ImportResult>(job.Result, NodusJson.Options));
    }

    private async Task<ImportResult> ApplyAsync(Guid userId, ImportParseResult parsed, IReadOnlySet<int>? only,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var existing = (await db.Nodes.AsNoTracking()
                .Where(n => n.OwnerId == userId && n.DeletedAt == null)
                .Select(n => new { n.Type, n.Title, n.Date, n.Time, n.Recurrence })
                .ToListAsync(ct))
            .Select(n => ActiveNodes.Fingerprint(n.Type, n.Title, n.Date, n.Time, n.Recurrence))
            .ToHashSet();

        var issues = new List<ImportIssue>(parsed.Issues);
        int created = 0, duplicates = 0, inactive = 0;
        foreach (var candidate in parsed.Candidates.Where(c => only is null || only.Contains(c.Index)))
        {
            if (created >= options.Value.MaxNodes)
            {
                issues.Add(new ImportIssue(candidate.Index, candidate.Input.Title, "too_many_nodes",
                    $"At most {options.Value.MaxNodes} nodes are imported at once."));
                continue;
            }

            var errors = new ValidationErrors();
            var template = NodeTemplate.Parse(candidate.Type, candidate.Input, errors);
            if (template is null)
            {
                issues.Add(new ImportIssue(candidate.Index, candidate.Input.Title, "invalid_node", "The node is incomplete or invalid."));
                continue;
            }

            if (!ActiveNodes.IsActive(candidate.Type, template.Date, template.Time, template.Recurrence, template.Timezone, now))
            {
                inactive++;
                continue;
            }

            if (!existing.Add(ActiveNodes.Fingerprint(candidate.Type, template.Title, template.Date, template.Time, template.Recurrence)))
            {
                duplicates++;
                continue;
            }

            if (await nodes.AddImportedAsync(userId, candidate.Type, candidate.Input, errors, ct) is not null) created++;
        }

        return new ImportResult(created, duplicates, inactive, issues);
    }

    private async Task<ApiResult> QueueAiImportAsync(Guid userId, string content, string? fileName, string timezone,
        CancellationToken ct)
    {
        var o = options.Value;
        if (content.Length > o.MaxAiInputChars)
            return ApiResult.Problem(StatusCodes.Status400BadRequest, "file_too_large_for_ai",
                $"Files of other apps are converted by AI and must be at most {o.MaxAiInputChars} characters.");
        var now = time.GetUtcNow();
        if (await db.ImportJobs.CountAsync(j => j.UserId == userId && j.CreatedAt > now.AddDays(-1), ct) >= o.MaxAiImportsPerDay)
            return ApiResult.Problem(StatusCodes.Status429TooManyRequests, "ai_quota_exceeded",
                "The daily limit of AI imports has been reached.");

        var job = new ImportJob
        {
            Id = Guid.NewGuid(), UserId = userId, Status = ImportJobStatus.Queued, InputText = content,
            FileName = fileName?.Length > 200 ? fileName[..200] : fileName, Timezone = timezone, CreatedAt = now,
            ExpiresAt = now.AddDays(o.PreviewRetentionDays)
        };
        db.ImportJobs.Add(job);
        db.OutboxEvents.Add(OutboxEvent.Create(OutboxEventTypes.ImportRequested,
            JsonSerializer.Serialize(new ImportRequestedPayload(job.Id), NodusJson.Options), now));
        Touch(job, now);
        await unitOfWork.CommitAsync(ct);
        return ApiResult.Accepted(new { mode = "ai", job = Dto(job) });
    }

    /// <summary>Request zone → the user's current zone → UTC.</summary>
    private async Task<ImportDefaults> DefaultsAsync(Guid userId, string? requested, CancellationToken ct)
    {
        var zone = ZonedTime.TryFindZone(requested, out _)
            ? requested!
            : await db.Users.Where(u => u.Id == userId).Select(u => u.Timezone).SingleAsync(ct) ?? "UTC";
        return new ImportDefaults(ZonedTime.TryFindZone(zone, out _) ? zone : "UTC", time.GetUtcNow());
    }

    /// <summary>A JSON object with string <c>source</c> and integer <c>version</c>.</summary>
    private static bool TryReadFormat(string content, out ImportFormat format, out JsonElement document)
    {
        format = default;
        document = default;
        try
        {
            using var parsed = JsonDocument.Parse(content);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var v))
                return false;
            format = new ImportFormat(source.GetString()!.Trim().ToLowerInvariant(), v);
            document = root.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
