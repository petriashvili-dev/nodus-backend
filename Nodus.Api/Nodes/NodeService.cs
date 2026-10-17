using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
            node.Revision, node.Version, node.CreatedAt, node.UpdatedAt, horizon,
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

        var created = new List<Node>();
        for (var i = 0; i < items.Count; i++)
        {
            var node = await AddAsync(userId, items[i], errors, $"nodes[{i}].", ct);
            if (node is null && !errors.HasErrors) return NodeExists(items[i].Id);
            if (node is not null) created.Add(node);
        }

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
        var occurrence = projection.Find(await histories.LoadAsync(node, ct), key, now);
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

    private async Task<Node?> AddAsync(Guid userId, CreateNodeRequest request, ValidationErrors errors, string prefix,
        CancellationToken ct)
    {
        errors.AddIf(request.Id == Guid.Empty, prefix + "id", "A client-generated UUID is required.")
            .AddIf(request.Type is null || !Enum.IsDefined(request.Type.Value), prefix + "type", "A node type is required.");
        if (request.Type is null || !Enum.IsDefined(request.Type.Value)) return null;
        var template = NodeTemplate.Parse(request.Type.Value, request, errors, prefix);
        if (template is null || errors.HasErrors) return null;
        // Ids are global; an id taken by any user is a conflict (without revealing whose).
        if (await db.Nodes.AnyAsync(n => n.Id == request.Id, ct)) return null;

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
