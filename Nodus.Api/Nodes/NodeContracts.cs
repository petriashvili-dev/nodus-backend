using Nodus.Api.Common;
using Nodus.Api.Occurrences;
using Nodus.Api.Recurrence;

namespace Nodus.Api.Nodes;

/// <summary>Editable template fields. PATCH sends the complete template the client has (not a field diff).</summary>
public record NodeTemplateInput
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Timezone { get; init; }

    /// <summary>One-time: scheduled date (optional). Anniversary: original date (required).</summary>
    public DateOnly? Date { get; init; }

    /// <summary>One-time and anniversary: local time, null for all-day. Recurring nodes put times in the rule.</summary>
    public TimeOnly? Time { get; init; }

    /// <summary>Recurring only.</summary>
    public RecurrenceRule? Recurrence { get; init; }

    /// <summary>Anniversary only.</summary>
    public bool? TrackYears { get; init; }
}

public sealed record CreateNodeRequest : NodeTemplateInput
{
    /// <summary>Client-generated UUID, so offline-created nodes keep their identity.</summary>
    public Guid Id { get; init; }

    public NodeType? Type { get; init; }
}

public sealed record UpdateNodeRequest : NodeTemplateInput
{
    public long? ExpectedVersion { get; init; }
}

public sealed record BatchCreateNodesRequest(IReadOnlyList<CreateNodeRequest>? Nodes);

public sealed record OccurrenceActionRequest(DateTimeOffset? CompletedAt);

public sealed record ChangeTimezoneRequest(string? Timezone);

public sealed record TimezoneAdaptation(string Timezone, int AdaptedNodes, int SkippedSharedNodes, int SkippedPastNodes);

/// <summary>Validated and normalised template.</summary>
public sealed record NodeTemplate(
    string Title,
    string? Description,
    string Timezone,
    DateOnly? Date,
    TimeOnly? Time,
    RecurrenceRule? Recurrence,
    bool TrackYears)
{
    public static NodeTemplate? Parse(NodeType type, NodeTemplateInput input, ValidationErrors errors, string prefix = "")
    {
        var title = input.Title?.Trim();
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        errors.AddIf(string.IsNullOrEmpty(title), prefix + "title", "Title is required.")
            .AddIf(title?.Length > Limits.TitleLength, prefix + "title", $"Title must be at most {Limits.TitleLength} characters.")
            .AddIf(description?.Length > Limits.DescriptionLength, prefix + "description",
                $"Description must be at most {Limits.DescriptionLength} characters.")
            .AddIf(!ZonedTime.TryFindZone(input.Timezone, out _), prefix + "timezone", "A valid IANA time zone is required.");

        var time = input.Time is { } t ? new TimeOnly(t.Hour, t.Minute) : (TimeOnly?)null;
        RecurrenceRule? rule = null;
        switch (type)
        {
            case NodeType.OneTime:
                errors.AddIf(input.Recurrence is not null, prefix + "recurrence", "One-time nodes have no recurrence.")
                    .AddIf(time is not null && input.Date is null, prefix + "time", "A time requires a date.");
                break;
            case NodeType.Recurring:
                errors.AddIf(input.Recurrence is null, prefix + "recurrence", "Recurring nodes require a recurrence rule.")
                    .AddIf(input.Date is not null || time is not null, prefix + "date",
                        "Recurring nodes define dates and times in the recurrence rule.");
                if (input.Recurrence is not null)
                {
                    rule = input.Recurrence.Normalize();
                    foreach (var message in rule.Validate()) errors.Add(prefix + "recurrence", message);
                }

                break;
            case NodeType.Anniversary:
                errors.AddIf(input.Date is null, prefix + "date", "Anniversaries require the original date.")
                    .AddIf(input.Recurrence is not null, prefix + "recurrence", "Anniversary recurrence is derived from the date.");
                if (input.Date is { } date)
                    rule = new RecurrenceRule
                    {
                        Frequency = RecurrenceFrequency.Yearly, StartDate = date, Times = time is { } at ? [at] : []
                    };
                break;
        }

        errors.AddIf(type != NodeType.Anniversary && input.TrackYears == true, prefix + "track_years",
            "Only anniversaries track years.");
        if (errors.HasErrors) return null;

        return new NodeTemplate(title!, description, input.Timezone!,
            type == NodeType.Recurring ? null : input.Date,
            type == NodeType.Recurring ? null : time,
            rule, type == NodeType.Anniversary && (input.TrackYears ?? true));
    }
}

public sealed record OccurrenceDto(
    Guid NodeId,
    string Key,
    int Revision,
    string Title,
    string? Description,
    DateOnly? Date,
    TimeOnly? Time,
    DateTimeOffset? ScheduledAt,
    int? Years,
    OccurrenceState State,
    DateTimeOffset? CompletedAt,
    bool IsPast)
{
    public static OccurrenceDto From(Occurrence o) => new(o.NodeId, o.Key, o.Revision, o.Title, o.Description, o.Date,
        o.Time, o.ScheduledAt, o.Years, o.State, o.CompletedAt, o.IsPast);
}

public sealed record HorizonDto(DateOnly From, DateOnly To);

public sealed record NodeDto(
    Guid Id,
    NodeType Type,
    Guid OwnerId,
    string Title,
    string? Description,
    string Timezone,
    DateOnly? Date,
    TimeOnly? Time,
    DateTimeOffset? ScheduledAt,
    RecurrenceRule? Recurrence,
    bool TrackYears,
    int Revision,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<NodeMember> Members,
    HorizonDto Horizon,
    IReadOnlyList<OccurrenceDto> Occurrences);

public sealed record OccurrenceStateDto(
    Guid NodeId,
    string Key,
    OccurrenceState State,
    DateTimeOffset? CompletedAt,
    long Version,
    DateTimeOffset UpdatedAt,
    Guid UpdatedBy,
    int Revision,
    string Title,
    DateOnly? Date,
    TimeOnly? Time,
    DateTimeOffset? ScheduledAt)
{
    public static string EntityId(Guid nodeId, string key) => $"{nodeId}:{key}";

    public static OccurrenceStateDto From(NodeOccurrenceState s) => new(s.NodeId, s.OccurrenceKey, s.State, s.CompletedAt,
        s.Version, s.UpdatedAt, s.UpdatedBy, s.Revision, s.Title, s.Date, s.Time, s.ScheduledAt);
}
