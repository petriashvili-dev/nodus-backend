using System.Globalization;
using System.Text.Json;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Llm;
using Nodus.Api.Nodes;
using Nodus.Api.Recurrence;

namespace Nodus.Api.AiDrafts;

public sealed record DraftIssue(string Code, string? Field, string Message);

/// <summary>An editable proposal; the client turns it into a <see cref="CreateNodeRequest"/> after review.</summary>
public sealed record NodeDraftDto(
    int Index,
    NodeType Type,
    string Title,
    string? Description,
    string Timezone,
    DateOnly? Date,
    TimeOnly? Time,
    RecurrenceRule? Recurrence,
    bool? TrackYears,
    IReadOnlyList<DraftIssue> Issues);

/// <summary>
/// Prompt and strict server-side validation of the model output. The model is told not to invent missing facts;
/// the server never fills gaps either: unparseable or contradictory fields are dropped and reported as issues
/// for the user to resolve, instead of being guessed.
/// </summary>
public static class NodeDraftParser
{
    public const string PromptVersion = "node-drafts-v1";
    public const int MaxDrafts = 20;

    private static readonly LlmJsonSchema Schema = new("node_drafts", JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["nodes"],
          "properties": {
            "nodes": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["type", "title", "description", "date", "time", "recurrence", "track_years", "ambiguities"],
                "properties": {
                  "type": { "enum": ["one_time", "recurring", "anniversary"] },
                  "title": { "type": "string" },
                  "description": { "type": ["string", "null"] },
                  "date": { "type": ["string", "null"], "description": "YYYY-MM-DD" },
                  "time": { "type": ["string", "null"], "description": "HH:mm, 24h" },
                  "recurrence": {
                    "type": ["object", "null"],
                    "additionalProperties": false,
                    "required": ["frequency", "interval", "start_date", "until", "times", "week_days", "month_day"],
                    "properties": {
                      "frequency": { "enum": ["daily", "weekly", "monthly", "yearly"] },
                      "interval": { "type": "integer" },
                      "start_date": { "type": ["string", "null"] },
                      "until": { "type": ["string", "null"] },
                      "times": { "type": "array", "items": { "type": "string" } },
                      "week_days": { "type": "array", "items": { "enum": ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"] } },
                      "month_day": { "type": ["integer", "null"] }
                    }
                  },
                  "track_years": { "type": ["boolean", "null"] },
                  "ambiguities": { "type": "array", "items": { "type": "string" } }
                }
              }
            }
          }
        }
        """).RootElement.Clone());

    public static LlmRequest BuildRequest(string text, string timezone, DateTimeOffset referenceTime)
    {
        var local = TimeZoneInfo.ConvertTime(referenceTime, ZonedTime.FindZone(timezone));
        var reference = local.ToString("yyyy-MM-dd HH:mm, dddd", CultureInfo.GetCultureInfo("ru-RU"));
        var system = $$"""
            Ты разбираешь продиктованную фразу на отдельные дела для приложения-планировщика Nodus.
            Текущие дата и время пользователя: {{reference}} (часовой пояс {{timezone}}).
            Правила:
            - Каждое отдельное намерение — отдельный элемент "nodes". Не объединяй и не дроби без причины.
            - type: "one_time" — разовое дело; "recurring" — повторяющееся (каждый день, по вторникам, раз в месяц…);
              "anniversary" — день рождения, годовщина и другие ежегодные даты.
            - title — коротко, в повелительной или назывной форме, на языке пользователя.
            - Относительные даты ("завтра", "в пятницу", "через неделю") переводи в абсолютные относительно текущей даты.
            - Не выдумывай отсутствующие сведения: если дата или время не названы — null. Если сказано неоднозначно
              ("утром", "на днях", "в начале месяца") — оставь поле null и опиши неоднозначность в "ambiguities".
            - Для "recurring" дату и время указывай только внутри "recurrence" (times — список "HH:mm"); date и time — null.
            - Для "anniversary" date — исходная дата события; track_years = true, только если известен год.
            - description — только то, что пользователь явно сказал сверх названия, иначе null.
            Ответь строго JSON-объектом вида {"nodes": [...]} без пояснений.
            """;
        return new LlmRequest("node-drafts", [LlmMessage.System(system), LlmMessage.User(text)], Schema, Temperature: 0.1);
    }

    /// <exception cref="LlmException">The output is not the expected JSON structure (worth a retry).</exception>
    public static IReadOnlyList<NodeDraftDto> Parse(string content, string timezone, DateTimeOffset referenceTime, string provider)
    {
        JsonElement nodes;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("nodes", out nodes) || nodes.ValueKind != JsonValueKind.Array)
                throw new LlmException(LlmErrorCategory.InvalidResponse, provider, "Drafts JSON has no 'nodes' array.");
            nodes = nodes.Clone();
        }
        catch (JsonException e)
        {
            throw new LlmException(LlmErrorCategory.InvalidResponse, provider, "Drafts output is not JSON.", e);
        }

        var today = ZonedTime.LocalDate(referenceTime, ZonedTime.FindZone(timezone));
        var drafts = new List<NodeDraftDto>();
        foreach (var item in nodes.EnumerateArray().Take(MaxDrafts))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var draft = ParseDraft(item, drafts.Count, timezone, today);
            if (draft is not null) drafts.Add(draft);
        }

        return drafts;
    }

    private static NodeDraftDto? ParseDraft(JsonElement item, int index, string timezone, DateOnly today)
    {
        var issues = new List<DraftIssue>();
        var title = String(item, "title")?.Trim();
        if (string.IsNullOrEmpty(title)) return null;
        if (title.Length > Limits.TitleLength) title = title[..Limits.TitleLength];

        var type = String(item, "type") switch
        {
            "recurring" => NodeType.Recurring,
            "anniversary" => NodeType.Anniversary,
            _ => NodeType.OneTime
        };
        var date = Date(item, "date", issues);
        var time = Time(item, "time", issues);
        var recurrence = type == NodeType.Recurring ? Rule(item, today, issues) : null;
        var description = String(item, "description")?.Trim();
        if (description?.Length > Limits.DescriptionLength) description = description[..Limits.DescriptionLength];
        bool? trackYears = type == NodeType.Anniversary && item.TryGetProperty("track_years", out var ty) &&
                           ty.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? ty.GetBoolean()
            : null;

        if (type == NodeType.Recurring && recurrence is null)
        {
            // Without a rule this cannot be a series; keep it as an undated one-time draft to be fixed by the user.
            type = NodeType.OneTime;
            issues.Add(new DraftIssue("missing_recurrence", "recurrence", "Не удалось определить правило повторения."));
        }

        if (type == NodeType.Recurring) date = null;
        if (type == NodeType.Recurring) time = null;
        if (type == NodeType.OneTime && date is null && time is not null)
        {
            issues.Add(new DraftIssue("time_without_date", "date", "Указано время, но не дата."));
            time = null;
        }

        if (type == NodeType.Anniversary && date is null)
            issues.Add(new DraftIssue("missing_date", "date", "Не указана дата события."));
        if (type == NodeType.OneTime && date is { } d && d < today)
            issues.Add(new DraftIssue("past_date", "date", "Дата уже прошла."));

        if (item.TryGetProperty("ambiguities", out var ambiguities) && ambiguities.ValueKind == JsonValueKind.Array)
            issues.AddRange(ambiguities.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()))
                .Take(5)
                .Select(a => new DraftIssue("ambiguous", null, a.GetString()!.Trim())));

        // The same validation as node creation, so a confirmed draft never fails for a reason we could have shown.
        var errors = new ValidationErrors();
        NodeTemplate.Parse(type, new NodeTemplateInput
        {
            Title = title, Description = description, Timezone = timezone, Date = date, Time = time,
            Recurrence = recurrence, TrackYears = trackYears
        }, errors);
        if (errors.HasErrors)
            issues.Add(new DraftIssue("needs_review", null, "Черновик нужно дополнить перед сохранением."));

        return new NodeDraftDto(index, type, title, string.IsNullOrEmpty(description) ? null : description, timezone, date, time,
            recurrence, trackYears, issues);
    }

    private static RecurrenceRule? Rule(JsonElement item, DateOnly today, List<DraftIssue> issues)
    {
        if (!item.TryGetProperty("recurrence", out var r) || r.ValueKind != JsonValueKind.Object) return null;
        var frequency = String(r, "frequency") switch
        {
            "daily" => RecurrenceFrequency.Daily,
            "weekly" => RecurrenceFrequency.Weekly,
            "monthly" => RecurrenceFrequency.Monthly,
            "yearly" => RecurrenceFrequency.Yearly,
            _ => (RecurrenceFrequency?)null
        };
        if (frequency is null) return null;

        var times = r.TryGetProperty("times", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => ParseTime(x.ValueKind == JsonValueKind.String ? x.GetString() : null))
                .Where(x => x is not null).Select(x => x!.Value).ToArray()
            : [];
        var weekDays = r.TryGetProperty("week_days", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => Enum.TryParse<DayOfWeek>(x.GetString(), true, out var day) ? day : (DayOfWeek?)null)
                .Where(x => x is not null).Select(x => x!.Value).ToArray()
            : [];
        var rule = new RecurrenceRule
        {
            Frequency = frequency.Value,
            Interval = Int(r, "interval") is > 0 and var interval ? interval : 1,
            StartDate = Date(r, "start_date", issues) ?? today,
            Until = Date(r, "until", issues),
            Times = times,
            WeekDays = frequency == RecurrenceFrequency.Weekly ? weekDays : [],
            MonthDay = frequency == RecurrenceFrequency.Monthly ? Int(r, "month_day") : null
        }.Normalize();

        if (!rule.Validate().Any()) return rule;
        issues.Add(new DraftIssue("invalid_recurrence", "recurrence", "Правило повторения некорректно."));
        return null;
    }

    /// <summary>Null for absent, null or non-integer values (<c>TryGetInt32</c> throws on non-numbers).</summary>
    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateOnly? Date(JsonElement e, string name, List<DraftIssue> issues)
    {
        var raw = String(e, name);
        if (raw is null) return null;
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        issues.Add(new DraftIssue("invalid_date", name, "Не удалось распознать дату."));
        return null;
    }

    private static TimeOnly? Time(JsonElement e, string name, List<DraftIssue> issues)
    {
        var raw = String(e, name);
        if (raw is null) return null;
        var time = ParseTime(raw);
        if (time is null) issues.Add(new DraftIssue("invalid_time", name, "Не удалось распознать время."));
        return time;
    }

    private static TimeOnly? ParseTime(string? raw) =>
        TimeOnly.TryParseExact(raw, ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? new TimeOnly(t.Hour, t.Minute)
            : null;
}
