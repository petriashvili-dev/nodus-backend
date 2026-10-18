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
/// format parser and stores it as a preview (lifecycle in <see cref="AiJobRunner{TJob,TOutput}"/>). Nothing is created
/// until the user confirms; the uploaded text is erased when the conversion ends, successfully or not.
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
    : AiJobRunner<ImportJob, string>(db, unitOfWork, llm, logger)
{
    public const string PromptVersion = "import-v1";

    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [15, 60])]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public Task RunAsync(Guid jobId, CancellationToken ct) => ExecuteAsync(jobId, ct);

    protected override string Kind => "import";
    protected override string Table => "import_jobs";
    protected override int MaxAttempts => options.Value.MaxAttempts;

    protected override bool IsPending(ImportJob job) => job.Status is ImportJobStatus.Queued or ImportJobStatus.Processing;
    protected override bool IsProcessing(ImportJob job) => job.Status == ImportJobStatus.Processing;

    protected override void MarkProcessing(ImportJob job)
    {
        job.Status = ImportJobStatus.Processing;
        imports.Touch(job, time.GetUtcNow());
    }

    protected override LlmRequest BuildRequest(ImportJob job, string input) => BuildRequest(input, job.Timezone, time.GetUtcNow());

    protected override string Parse(ImportJob job, LlmResult result) => Normalize(result.Content, result.Provider);

    protected override Task<string?> CompleteAsync(ImportJob job, string preview, LlmResult result, CancellationToken ct)
    {
        job.Preview = preview;
        if (imports.ParsePreview(job).Candidates.Count == 0) return Task.FromResult<string?>("nothing_recognized");

        job.Status = ImportJobStatus.Ready;
        job.LastError = null;
        job.InputText = null;
        imports.Touch(job, time.GetUtcNow());
        Notify(job, ready: true);
        return Task.FromResult<string?>(null);
    }

    protected override void Fail(ImportJob job, string error)
    {
        job.Status = ImportJobStatus.Failed;
        job.Preview = null;
        job.LastError = error;
        job.InputText = null;
        imports.Touch(job, time.GetUtcNow());
        Notify(job, ready: false);
    }

    private void Notify(ImportJob job, bool ready) =>
        push.Request(new PushRequest(job.UserId, $"import-{job.Id}", PushKinds.ImportReady,
            ready ? "Импорт готов к проверке" : "Не получилось импортировать файл",
            ready ? "Проверьте узлы и подтвердите импорт." : "Откройте Nodus, чтобы попробовать ещё раз.",
            new Dictionary<string, string> { ["import_id"] = job.Id.ToString() }, CollapseId: $"import-{job.Id}"));

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
