using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;
using Nodus.Api.Sync;

namespace Nodus.Api.AiContinuations;

public sealed class ContinuationOptions
{
    public const string Section = "AiContinuations";

    public bool Enabled { get; init; } = true;

    /// <summary>Edits within this window collapse into one pipeline run (only the latest content is processed).</summary>
    public int DebounceSeconds { get; init; } = 20;

    public int MaxLlmCallsPerUserPerDay { get; init; } = 30;
}

public sealed record ContinuationRequestedPayload(Guid NodeId, string ContentHash);

public sealed record AcceptSuggestionRequest(CreateNodeRequest? Node);

public sealed record SuggestionDto(
    Guid Id,
    Guid SourceNodeId,
    SuggestionStatus Status,
    string? Title,
    string? Description,
    DateTimeOffset? ShownAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? DismissedAt,
    Guid? LinkedNodeId,
    long Version)
{
    public static SuggestionDto From(NodeSuggestion s) => new(s.Id, s.SourceNodeId, s.Status, s.ProposedTitle,
        s.ProposedDescription, s.ShownAt, s.AcceptedAt, s.DismissedAt, s.LinkedNodeId, s.Version);

    /// <summary>Only these states are synced; others are delivered as tombstones.</summary>
    public static bool IsVisible(NodeSuggestion s) =>
        s.Status is SuggestionStatus.Ready or SuggestionStatus.Accepted or SuggestionStatus.Dismissed;
}

/// <summary>Called by <see cref="NodeService"/> on every create/edit; never delays the API response.</summary>
public sealed class ContinuationTrigger(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    IOptions<ContinuationOptions> options,
    TimeProvider time)
{
    /// <summary>
    /// Requests the pipeline when the semantic content of a one-time node changed, and marks a no longer matching,
    /// not yet accepted suggestion stale in the same transaction.
    /// </summary>
    public async Task OnNodeChangedAsync(Node node, CancellationToken ct)
    {
        if (!options.Value.Enabled) return;
        var hash = ContinuationRules.ContentHash(node.Title, node.Description);
        var existing = await db.NodeSuggestions.SingleOrDefaultAsync(s => s.SourceNodeId == node.Id, ct);
        if (existing?.ContentHash == hash && node.DeletedAt is null) return;

        var now = time.GetUtcNow();
        if (existing is { Status: SuggestionStatus.Ready or SuggestionStatus.Skipped })
        {
            var wasVisible = SuggestionDto.IsVisible(existing);
            existing.Status = SuggestionStatus.Stale;
            existing.UpdatedAt = now;
            existing.Version++;
            if (wasVisible) Record(unitOfWork, existing);
        }

        if (node.Type != NodeType.OneTime || node.DeletedAt is not null) return;
        db.OutboxEvents.Add(OutboxEvent.Create(OutboxEventTypes.AiContinuationRequested,
            JsonSerializer.Serialize(new ContinuationRequestedPayload(node.Id, hash), NodusJson.Options), now));
    }

    public static void Record(UnitOfWork unitOfWork, NodeSuggestion suggestion) =>
        unitOfWork.Journal.Record(SyncEntityType.Suggestion, suggestion.Id.ToString(), suggestion.Version, SyncOperation.Upsert,
            [suggestion.UserId]);
}

public sealed class SuggestionService(NodusDbContext db, UnitOfWork unitOfWork, NodeService nodes, TimeProvider time)
{
    /// <summary>Records the single display; repeated calls keep the first timestamp.</summary>
    public async Task<ApiResult> MarkShownAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var suggestion = await FindAsync(userId, id, ct);
        if (suggestion is null) return ApiResult.NotFound("Suggestion");
        if (suggestion.ShownAt is null)
        {
            suggestion.ShownAt = time.GetUtcNow();
            Touch(suggestion);
            await unitOfWork.CommitAsync(ct);
        }

        return ApiResult.Ok(SuggestionDto.From(suggestion));
    }

    /// <summary>
    /// One idempotent operation: creates the (possibly edited) follow-up node and marks the suggestion accepted,
    /// so an offline client can queue it as a single outbox entry.
    /// </summary>
    public async Task<ApiResult> AcceptAsync(Guid userId, Guid id, AcceptSuggestionRequest request, CancellationToken ct)
    {
        var suggestion = await FindAsync(userId, id, ct);
        if (suggestion is null) return ApiResult.NotFound("Suggestion");
        if (suggestion.Status == SuggestionStatus.Accepted)
            return ApiResult.Problem(StatusCodes.Status409Conflict, "already_accepted", "The suggestion was already accepted.",
                extensions: new Dictionary<string, object?> { ["linked_node_id"] = suggestion.LinkedNodeId });
        if (suggestion.Status != SuggestionStatus.Ready)
            return ApiResult.Problem(StatusCodes.Status409Conflict, "suggestion_unavailable", "The suggestion is no longer available.");
        if (request.Node is null) return new ValidationErrors().Add("node", "The node to create is required.").ToResult();

        var created = await nodes.CreateAsync(userId, request.Node, ct);
        if (!created.IsSuccess) return created;

        var now = time.GetUtcNow();
        suggestion.Status = SuggestionStatus.Accepted;
        suggestion.AcceptedAt = now;
        suggestion.ShownAt ??= now;
        suggestion.LinkedNodeId = request.Node.Id;
        Touch(suggestion);
        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(new { suggestion = SuggestionDto.From(suggestion), node = created.Value });
    }

    public async Task<ApiResult> DismissAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var suggestion = await FindAsync(userId, id, ct);
        if (suggestion is null) return ApiResult.NotFound("Suggestion");
        if (suggestion.Status == SuggestionStatus.Ready)
        {
            var now = time.GetUtcNow();
            suggestion.Status = SuggestionStatus.Dismissed;
            suggestion.DismissedAt = now;
            suggestion.ShownAt ??= now;
            Touch(suggestion);
            await unitOfWork.CommitAsync(ct);
        }

        return ApiResult.Ok(SuggestionDto.From(suggestion));
    }

    private Task<NodeSuggestion?> FindAsync(Guid userId, Guid id, CancellationToken ct) =>
        db.NodeSuggestions.SingleOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);

    private void Touch(NodeSuggestion suggestion)
    {
        suggestion.UpdatedAt = time.GetUtcNow();
        suggestion.Version++;
        ContinuationTrigger.Record(unitOfWork, suggestion);
    }
}
