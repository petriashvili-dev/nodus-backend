using System.Globalization;
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
using Nodus.Api.Recurrence;
using Nodus.Api.Sync;

namespace Nodus.Api.DataPortability;

public sealed class ImportRequestedOutboxHandler(IBackgroundJobs jobs) : IOutboxHandler
{
    public string EventType => OutboxEventTypes.ImportRequested;

    public Task HandleAsync(OutboxEvent outboxEvent, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ImportRequestedPayload>(outboxEvent.Payload, NodusJson.Options)!;
        return jobs.EnqueueAsync<ImportRunner>(r => r.RunAsync(payload.JobId, CancellationToken.None), HangfireQueues.Imports);
    }
}

/// <summary>
/// Converts a foreign file into the current Nodus format with the model, validates the result with the current
/// format parser and stores it as a preview. Nothing is created until the user confirms.
/// </summary>
public sealed class ImportRunner(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    ImportService imports,
    ILlmClient llm,
    PushNotifier push,
    IOptions<ImportOptions> options,
    TimeProvider time,
    ILogger<ImportRunner> logger)
{
    public const string PromptVersion = "import-v1";

    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [15, 60])]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.ImportJobs.SingleOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status is not (ImportJobStatus.Queued or ImportJobStatus.Processing)) return;
        if (job.InputText is null)
        {
            await FinishAsync(job, null, "input_expired", ct);
            return;
        }

        job.Status = ImportJobStatus.Processing;
        job.Attempts++;
        imports.Touch(job, time.GetUtcNow());
        await unitOfWork.CommitAsync(ct);

        string preview;
        try
        {
            var result = await llm.GenerateAsync(BuildRequest(job.InputText, job.Timezone, time.GetUtcNow()), ct);
            preview = Normalize(result.Content, result.Provider);
        }
        catch (LlmException e) when (e.IsTransient && job.Attempts < options.Value.MaxAttempts)
        {
            logger.LogWarning("Import {JobId} attempt {Attempt} failed: {Category}", job.Id, job.Attempts, e.Category);
            job.LastError = e.Category.ToString();
            await db.SaveChangesAsync(ct);
            throw;
        }
        catch (LlmException e)
        {
            logger.LogError("Import {JobId} failed: {Category}", job.Id, e.Category);
            await FinishAsync(job, null, e.Category is LlmErrorCategory.InvalidResponse ? "invalid_ai_response" : "ai_unavailable", ct);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var current = await db.ImportJobs.FromSql($"SELECT * FROM import_jobs WHERE id = {jobId} FOR UPDATE").SingleAsync(ct);
        if (current.Status != ImportJobStatus.Processing) return; // cancelled meanwhile

        current.Preview = preview;
        var empty = imports.ParsePreview(current).Candidates.Count == 0;
        await FinishAsync(current, empty ? null : preview, empty ? "nothing_recognized" : null, ct);
        await transaction.CommitAsync(ct);
        unitOfWork.AfterCommit();
    }

    /// <summary>Ready with a preview, or failed; the uploaded text is erased either way.</summary>
    private async Task FinishAsync(ImportJob job, string? preview, string? error, CancellationToken ct)
    {
        job.Status = error is null ? ImportJobStatus.Ready : ImportJobStatus.Failed;
        job.Preview = preview;
        job.LastError = error;
        job.InputText = null;
        imports.Touch(job, time.GetUtcNow());
        push.Request(new PushRequest(job.UserId, $"import-{job.Id}", PushKinds.ImportReady,
            error is null ? "Импорт готов к проверке" : "Не получилось импортировать файл",
            error is null ? "Проверьте узлы и подтвердите импорт." : "Откройте Nodus, чтобы попробовать ещё раз.",
            new Dictionary<string, string> { ["import_id"] = job.Id.ToString() }, CollapseId: $"import-{job.Id}"));
        await unitOfWork.CommitAsync(ct);
    }

    /// <summary>The model answers with the document; source and version are forced to the current format.</summary>
    internal static string Normalize(string content, string provider)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                throw new LlmException(LlmErrorCategory.InvalidResponse, provider, "Converted file has no 'nodes' array.");
            var timezone = document.RootElement.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.String
                ? tz.GetString()
                : null;
            return JsonSerializer.Serialize(new
            {
                source = ImportFormat.NodusSource, version = ImportFormat.CurrentVersion, timezone, nodes
            }, NodusJson.Options);
        }
        catch (JsonException e)
        {
            throw new LlmException(LlmErrorCategory.InvalidResponse, provider, "Converted file is not JSON.", e);
        }
    }

    internal static LlmRequest BuildRequest(string text, string timezone, DateTimeOffset now)
    {
        var today = ZonedTime.LocalDate(now, ZonedTime.FindZone(timezone)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var system = $$"""
            Ты конвертируешь экспорт другого приложения (задачи, напоминания, календарь, привычки, дни рождения)
            в формат планировщика Nodus. Сегодня {{today}}, часовой пояс пользователя по умолчанию {{timezone}}.
            Формат ответа — строго JSON:
            {"timezone": "<IANA или null>", "nodes": [
              {"type": "one_time|recurring|anniversary", "title": "...", "description": "... или null",
               "timezone": "<IANA или null>", "date": "YYYY-MM-DD или null", "time": "HH:mm или null",
               "recurrence": null или {"frequency": "daily|weekly|monthly|yearly", "interval": 1, "start_date": "YYYY-MM-DD или null",
                 "until": "YYYY-MM-DD или null", "times": ["HH:mm"], "week_days": ["monday"...], "month_day": null},
               "track_years": true|false|null}
            ]}
            Правила:
            - one_time — разовое дело; recurring — повторяющееся (дата и время только внутри recurrence, date и time = null);
              anniversary — день рождения, годовщина (date — исходная дата, track_years = true, если известен год).
            - Переноси только сведения из файла. Не выдумывай даты, время, описания; чего нет — null.
            - Пропускай выполненные, удалённые и архивные элементы, если файл их так помечает.
            - Время — 24-часовое HH:mm, даты — YYYY-MM-DD; относительные даты переводи относительно сегодняшней.
            - Название — как в файле, без служебных пометок (приоритеты, теги, номера).
            """;
        return new LlmRequest("import", [LlmMessage.System(system), LlmMessage.User(text)], Schema, Temperature: 0,
            MaxTokens: 8000);
    }

    private static readonly LlmJsonSchema Schema = new("nodus_import", JsonDocument.Parse("""
        {
          "type": "object", "additionalProperties": false, "required": ["timezone", "nodes"],
          "properties": {
            "timezone": { "type": ["string", "null"] },
            "nodes": { "type": "array", "items": {
              "type": "object", "additionalProperties": false,
              "required": ["type", "title", "description", "timezone", "date", "time", "recurrence", "track_years"],
              "properties": {
                "type": { "enum": ["one_time", "recurring", "anniversary"] },
                "title": { "type": "string" },
                "description": { "type": ["string", "null"] },
                "timezone": { "type": ["string", "null"] },
                "date": { "type": ["string", "null"] },
                "time": { "type": ["string", "null"] },
                "recurrence": { "type": ["object", "null"], "additionalProperties": false,
                  "required": ["frequency", "interval", "start_date", "until", "times", "week_days", "month_day"],
                  "properties": {
                    "frequency": { "enum": ["daily", "weekly", "monthly", "yearly"] },
                    "interval": { "type": "integer" },
                    "start_date": { "type": ["string", "null"] },
                    "until": { "type": ["string", "null"] },
                    "times": { "type": "array", "items": { "type": "string" } },
                    "week_days": { "type": "array", "items": { "type": "string" } },
                    "month_day": { "type": ["integer", "null"] } } },
                "track_years": { "type": ["boolean", "null"] } } } }
          }
        }
        """).RootElement.Clone());
}
