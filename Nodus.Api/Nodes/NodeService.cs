using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.AiContinuations;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Occurrences;
using Nodus.Api.Recurrence;
using Nodus.Api.Sync;

namespace Nodus.Api.Nodes;

public sealed class OccurrenceOptions
{
    public const string Section = "Occurrences";

    /// <summary>Days ahead of today that node DTOs carry ready occurrences for (local notifications, offline feed).</summary>
    public int HorizonDays { get; init; } = 45;

    /// <summary>How far back a client-supplied completion time is accepted (offline unties).</summary>
    public int MaxCompletionBackdateDays { get; init; } = 30;
}

/// <summary>Builds node DTOs with the occurrence horizon.</summary>
public sealed class NodeDtoFactory(IRecurrenceProjectionService projection, IOptions<OccurrenceOptions> options, TimeProvider time)
{
    public NodeDto Create(NodeHistory history)
    {
        var node = history.Node;
        var now = time.GetUtcNow();
        var today = ZonedTime.LocalDate(now, ZonedTime.FindZone(node.Timezone));
        var horizon = new HorizonDto(today.AddDays(-1), today.AddDays(options.Value.HorizonDays));
        var occurrences = node.Type == NodeType.OneTime
            ? projection.Highlights(history, now)
            : projection.Project(history, horizon.From, horizon.To, now);

        return new NodeDto(node.Id, node.Type, node.OwnerId, node.Title, node.Description, node.Timezone, node.Date,
            node.Time, node.ScheduledAt, node.Type == NodeType.Recurring ? node.Recurrence : null, node.TrackYears,
            node.Revision, node.Version, node.CreatedAt, node.UpdatedAt, history.Members ?? [], horizon,
            occurrences.Select(OccurrenceDto.From).ToList());
    }
}

public sealed class NodeService(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    NodeAccess access,
    NodeHistoryLoader histories,
    NodeDtoFactory dtos,
    IRecurrenceProjectionService projection,
    IOptions<OccurrenceOptions> options,
    ContinuationTrigger continuations,
    TimeProvider time)
{
    public async Task<ApiResult> CreateAsync(Guid userId, CreateNodeRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var node = await AddAsync(userId, request, errors, "", ct);
        if (node is null) return errors.HasErrors ? errors.ToResult() : NodeExists();

        await unitOfWork.CommitAsync(ct);
        return ApiResult.Created(dtos.Create(await histories.LoadAsync(node, ct)));
    }

    /// <summary>All-or-nothing creation; the response lists the created nodes in request order under their client ids.</summary>
    public async Task<ApiResult> CreateBatchAsync(Guid userId, BatchCreateNodesRequest request, CancellationToken ct)
    {
        var items = request.Nodes ?? [];
        var errors = new ValidationErrors()
            .AddIf(items.Count == 0, "nodes", "At least one node is required.")
            .AddIf(items.Count > Limits.BatchSize, "nodes", $"At most {Limits.BatchSize} nodes per batch.")
            .AddIf(items.Select(n => n.Id).Distinct().Count() != items.Count, "nodes", "Node ids must be unique.");
        if (errors.HasErrors) return errors.ToResult();

        // One query for all ids instead of one per node.
        var ids = items.Select(n => n.Id).ToArray();
        if (await db.Nodes.Where(n => ids.Contains(n.Id)).Select(n => (Guid?)n.Id).FirstOrDefaultAsync(ct) is { } taken)
            return NodeExists(taken);

        var created = new List<Node>();
        for (var i = 0; i < items.Count; i++)
            if (await AddAsync(userId, items[i], errors, $"nodes[{i}].", ct, idChecked: true) is { } node)
                created.Add(node);

        if (errors.HasErrors) return errors.ToResult();

        await unitOfWork.CommitAsync(ct);
        var loaded = await histories.LoadAsync(created, ct);
        return ApiResult.Created(new { nodes = created.Select(n => dtos.Create(loaded[n.Id])).ToList() });
    }

    public async Task<ApiResult> UpdateAsync(Guid userId, Guid nodeId, UpdateNodeRequest request, CancellationToken ct)
    {
        var node = await access.FindAsync(userId, nodeId, ct);
        if (node is null || node.DeletedAt is not null) return ApiResult.NotFound("Node");
        if (request.ExpectedVersion is null)
            return new ValidationErrors().Add("expected_version", "The version the edit is based on is required.").ToResult();
        if (request.ExpectedVersion != node.Version) return await VersionConflictAsync(node, ct);

        var errors = new ValidationErrors();
        var template = NodeTemplate.Parse(node.Type, request, errors);
        if (template is null) return errors.ToResult();

        if (!Differs(node, template)) return ApiResult.Ok(dtos.Create(await histories.LoadAsync(node, ct)));

        var now = time.GetUtcNow();
        if (node.IsSeries)
        {
            if (template.Recurrence!.Frequency != node.Recurrence!.Frequency) node.Generation++;
            node.Revision++;
            db.NodeRevisions.Add(Revision(node, template, now, userId, effectiveFrom: now));
        }

        Apply(node, template);
        node.Version++;
        node.UpdatedAt = now;
        node.UpdatedBy = userId;
        unitOfWork.Journal.Record(SyncEntityType.Node, node.Id.ToString(), node.Version, SyncOperation.Upsert,
            await access.RecipientsAsync(node, ct));
        await continuations.OnNodeChangedAsync(node, ct);

        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(dtos.Create(await histories.LoadAsync(node, ct)));
    }

    /// <summary>
    /// Soft delete: the node leaves feeds and clients receive a tombstone, while revisions and occurrence states stay
    /// in the database so history is not destroyed.
    /// </summary>
    public async Task<ApiResult> DeleteAsync(Guid userId, Guid nodeId, long? expectedVersion, CancellationToken ct)
    {
        var node = await access.FindAsync(userId, nodeId, ct);
        if (node is null) return ApiResult.NotFound("Node");
        if (node.DeletedAt is not null) return ApiResult.NoContent();
        if (!NodeAccess.CanDelete(node, userId))
            return ApiResult.Problem(StatusCodes.Status403Forbidden, "forbidden", "Only the owner can delete this node.");
        if (expectedVersion is not null && expectedVersion != node.Version) return await VersionConflictAsync(node, ct);

        var now = time.GetUtcNow();
        node.DeletedAt = now;
        node.UpdatedAt = now;
        node.UpdatedBy = userId;
        node.Version++;
        unitOfWork.Journal.Record(SyncEntityType.Node, node.Id.ToString(), node.Version, SyncOperation.Delete,
            await access.RecipientsAsync(node, ct));
        await continuations.OnNodeChangedAsync(node, ct);

        await unitOfWork.CommitAsync(ct);
        return ApiResult.NoContent();
    }

    /// <summary>Untie/retie one occurrence. Repeating the current state is a no-op, so retries are harmless.</summary>
    public async Task<ApiResult> SetOccurrenceStateAsync(Guid userId, Guid nodeId, string key, OccurrenceState target,
        DateTimeOffset? completedAt, CancellationToken ct)
    {
        var node = await access.FindAsync(userId, nodeId, ct);
        if (node is null || node.DeletedAt is not null) return ApiResult.NotFound("Node");

        var now = time.GetUtcNow();
        // The key may name an old occurrence (offline untie up to 30 days back): load states from its date.
        var statesFrom = OccurrenceKey.TryParse(key, out _, out var keyDate, out _) ? keyDate : histories.CurrentViewFrom;
        var occurrence = projection.Find(await histories.LoadAsync(node, statesFrom, ct), key, now);
        if (occurrence is null)
            return ApiResult.Problem(StatusCodes.Status404NotFound, "occurrence_not_found", "The node has no such occurrence.");

        var state = await db.NodeOccurrenceStates.SingleOrDefaultAsync(s => s.NodeId == nodeId && s.OccurrenceKey == key, ct);
        if (state?.State == target) return ApiResult.Ok(OccurrenceStateDto.From(state));

        if (state is null)
        {
            state = new NodeOccurrenceState { NodeId = nodeId, OccurrenceKey = key, Title = occurrence.Title };
            db.NodeOccurrenceStates.Add(state);
        }

        var earliest = now.AddDays(-options.Value.MaxCompletionBackdateDays);
        state.State = target;
        state.CompletedAt = target == OccurrenceState.Untied
            ? completedAt is { } at && at <= now && at >= earliest ? at : now
            : null;
        state.UpdatedAt = now;
        state.UpdatedBy = userId;
        state.Version++;
        state.Revision = occurrence.Revision;
        state.Title = occurrence.Title;
        state.Date = occurrence.Date;
        state.Time = occurrence.Time;
        state.ScheduledAt = occurrence.ScheduledAt;
        unitOfWork.Journal.Record(SyncEntityType.OccurrenceState, OccurrenceStateDto.EntityId(nodeId, key), state.Version,
            SyncOperation.Upsert, await access.RecipientsAsync(node, ct));

        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(OccurrenceStateDto.From(state));
    }

    /// <summary>
    /// Moves the user's own nodes to <paramref name="timezone"/>, keeping their local wall time: "09:00" stays 09:00,
    /// now in the new zone. Series get a new revision effective now, so past occurrences keep their historical zone.
    /// Skipped: shared nodes (their time is common to all participants and must not move because one of them
    /// travelled) and one-time nodes already in the past (history is not rewritten). The caller commits.
    /// </summary>
    public async Task<TimezoneAdaptation> AdaptToTimezoneAsync(Guid userId, string timezone, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var nodes = await db.Nodes
            .Where(n => n.OwnerId == userId && n.DeletedAt == null && n.Timezone != timezone)
            .ToListAsync(ct);
        var ids = nodes.Select(n => n.Id).ToArray();
        var shared = (await db.SharedNodeMembers.Where(m => ids.Contains(m.NodeId)).Select(m => m.NodeId).Distinct()
            .ToListAsync(ct)).ToHashSet();

        var result = new TimezoneAdaptation(timezone, 0, 0, 0);
        foreach (var node in nodes)
        {
            if (shared.Contains(node.Id))
            {
                result = result with { SkippedSharedNodes = result.SkippedSharedNodes + 1 };
                continue;
            }

            if (node.Type == NodeType.OneTime && IsPast(node, now))
            {
                result = result with { SkippedPastNodes = result.SkippedPastNodes + 1 };
                continue;
            }

            var template = new NodeTemplate(node.Title, node.Description, timezone, node.Date, node.Time, node.Recurrence,
                node.TrackYears);
            if (node.IsSeries)
            {
                node.Revision++;
                db.NodeRevisions.Add(Revision(node, template, now, userId, effectiveFrom: now));
            }

            Apply(node, template);
            node.Version++;
            node.UpdatedAt = now;
            node.UpdatedBy = userId;
            unitOfWork.Journal.Record(SyncEntityType.Node, node.Id.ToString(), node.Version, SyncOperation.Upsert, [userId]);
            result = result with { AdaptedNodes = result.AdaptedNodes + 1 };
        }

        return result;
    }

    private static bool IsPast(Node node, DateTimeOffset now) =>
        node.ScheduledAt is { } at
            ? at <= now
            : node.Date is { } date && ZonedTime.StartOfDay(date.AddDays(1), ZonedTime.FindZone(node.Timezone)) <= now;

    /// <summary>
    /// Adds an imported node with a new id; the caller commits. Continuations are not requested: imported nodes are
    /// not something the user has just written, and a large import must not fan out into model calls.
    /// </summary>
    public Task<Node?> AddImportedAsync(Guid userId, NodeType type, NodeTemplateInput input, ValidationErrors errors,
        CancellationToken ct) =>
        AddAsync(userId, new CreateNodeRequest
        {
            Id = Guid.NewGuid(), Type = type, Title = input.Title, Description = input.Description, Timezone = input.Timezone,
            Date = input.Date, Time = input.Time, Recurrence = input.Recurrence, TrackYears = input.TrackYears
        }, errors, "", ct, triggerContinuations: false, idChecked: true); // server-generated id

    private async Task<Node?> AddAsync(Guid userId, CreateNodeRequest request, ValidationErrors errors, string prefix,
        CancellationToken ct, bool triggerContinuations = true, bool idChecked = false)
    {
        errors.AddIf(request.Id == Guid.Empty, prefix + "id", "A client-generated UUID is required.")
            .AddIf(request.Type is null || !Enum.IsDefined(request.Type.Value), prefix + "type", "A node type is required.");
        if (request.Type is null || !Enum.IsDefined(request.Type.Value)) return null;
        var template = NodeTemplate.Parse(request.Type.Value, request, errors, prefix);
        if (template is null || errors.HasErrors) return null;
        // Ids are global; an id taken by any user is a conflict (without revealing whose).
        if (!idChecked && await db.Nodes.AnyAsync(n => n.Id == request.Id, ct)) return null;

        var now = time.GetUtcNow();
        var node = new Node
        {
            Id = request.Id, OwnerId = userId, Type = request.Type.Value, Title = template.Title, Timezone = template.Timezone,
            Version = 1, CreatedAt = now, UpdatedAt = now, UpdatedBy = userId
        };
        Apply(node, template);
        db.Nodes.Add(node);
        if (node.IsSeries)
        {
            node.Revision = 1;
            db.NodeRevisions.Add(Revision(node, template, now, userId, effectiveFrom: null));
        }

        unitOfWork.Journal.Record(SyncEntityType.Node, node.Id.ToString(), node.Version, SyncOperation.Upsert, [userId]);
        if (triggerContinuations) await continuations.OnNodeCreatedAsync(node, ct);
        return node;
    }

    private static void Apply(Node node, NodeTemplate template)
    {
        node.Title = template.Title;
        node.Description = template.Description;
        node.Timezone = template.Timezone;
        node.Date = template.Date;
        node.Time = template.Time;
        node.Recurrence = template.Recurrence;
        node.TrackYears = template.TrackYears;
        node.ScheduledAt = node.Type == NodeType.OneTime && template is { Date: { } date, Time: { } at }
            ? ZonedTime.ToInstant(date, at, ZonedTime.FindZone(template.Timezone))
            : null;
    }

    private static bool Differs(Node node, NodeTemplate template) =>
        node.Title != template.Title || node.Description != template.Description || node.Timezone != template.Timezone ||
        node.Date != template.Date || node.Time != template.Time || node.TrackYears != template.TrackYears ||
        !Equals(node.Recurrence, template.Recurrence);

    private static NodeRevision Revision(Node node, NodeTemplate template, DateTimeOffset now, Guid userId,
        DateTimeOffset? effectiveFrom) => new()
    {
        NodeId = node.Id, Revision = node.Revision, EffectiveFrom = effectiveFrom, Title = template.Title,
        Description = template.Description, Timezone = template.Timezone, Recurrence = template.Recurrence!,
        Generation = node.Generation, TrackYears = template.TrackYears, CreatedAt = now, CreatedBy = userId
    };

    private async Task<ApiResult> VersionConflictAsync(Node node, CancellationToken ct) =>
        ApiResult.Problem(StatusCodes.Status409Conflict, ProblemCodes.VersionConflict,
            "The node was changed since the version the edit is based on.",
            "Apply the canonical state from 'current' and resolve the edit explicitly.",
            new Dictionary<string, object?> { ["current"] = dtos.Create(await histories.LoadAsync(node, ct)) });

    private static ApiResult NodeExists(Guid? id = null) =>
        ApiResult.Problem(StatusCodes.Status409Conflict, "node_already_exists",
            id is null ? "A node with this id already exists." : $"A node with id {id} already exists.");
}
