using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Hangfire;
using Nodus.Api.Infrastructure.Llm;
using Nodus.Api.Infrastructure.Observability;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;
using Nodus.Api.Occurrences;
using Nodus.Api.Sync;

namespace Nodus.Api.AiContinuations;

public sealed class ContinuationRequestedOutboxHandler(IBackgroundJobs jobs, IOptions<ContinuationOptions> options) : IOutboxHandler
{
    public string EventType => OutboxEventTypes.AiContinuationRequested;

    public Task HandleAsync(OutboxEvent outboxEvent, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ContinuationRequestedPayload>(outboxEvent.Payload, NodusJson.Options)!;
        return jobs.ScheduleAsync<ContinuationRunner>(r => r.RunAsync(payload.NodeId, payload.ContentHash, CancellationToken.None),
            TimeSpan.FromSeconds(options.Value.DebounceSeconds));
    }
}

/// <summary>
/// HardFilter → SemanticRules → Templates → NormalizedCache → LLM. Runs after the debounce delay and only for the
/// content it was requested for; the source is re-checked after the model call, so a late result for edited content
/// is dropped instead of shown.
/// </summary>
public sealed class ContinuationRunner(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    ILlmClient llm,
    IOptions<ContinuationOptions> options,
    IOptions<EmailOtpOptions> secrets,
    TimeProvider time,
    ILogger<ContinuationRunner> logger)
{
    public const string PromptVersion = "continuation-v1";

    [Queue(HangfireQueues.AiContinuations)]
    [AutomaticRetry(Attempts = 0)] // a missing suggestion is acceptable; retries would only add cost
    public async Task RunAsync(Guid nodeId, string contentHash, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null || !await StillCurrentAsync(node, contentHash, ct)) return;

        var suggestion = await db.NodeSuggestions.SingleOrDefaultAsync(s => s.SourceNodeId == nodeId, ct);
        if (suggestion is { Status: SuggestionStatus.Accepted }) return;
        if (suggestion?.ContentHash == contentHash && suggestion.Status != SuggestionStatus.Stale) return; // already done

        var (title, origin, calledLlm) = await DecideAsync(node, ct);
        if (!await StillCurrentAsync(node, contentHash, ct)) return;

        var now = time.GetUtcNow();
        var wasVisible = suggestion is not null && SuggestionDto.IsVisible(suggestion);
        if (suggestion is null)
        {
            suggestion = new NodeSuggestion
            {
                Id = Guid.NewGuid(), UserId = node.OwnerId, SourceNodeId = node.Id, ContentHash = contentHash, CreatedAt = now
            };
            db.NodeSuggestions.Add(suggestion);
        }

        suggestion.ContentHash = contentHash;
        suggestion.SourceVersion = node.Version;
        suggestion.Status = title is null ? SuggestionStatus.Skipped : SuggestionStatus.Ready;
        suggestion.Origin = title is null ? null : origin;
        suggestion.ProposedTitle = title;
        suggestion.ProposedDescription = null;
        suggestion.PromptVersion = origin == SuggestionOrigin.Template ? ContinuationRules.TemplatesVersion : PromptVersion;
        suggestion.ShownAt = suggestion.DismissedAt = suggestion.AcceptedAt = null;
        suggestion.LinkedNodeId = null;
        if (calledLlm) suggestion.LlmCalledAt = now;
        NodusMetrics.Continuation(title is null ? $"skipped_{origin}".ToLowerInvariant() : $"ready_{origin}".ToLowerInvariant());
        suggestion.UpdatedAt = now;
        suggestion.Version++;
        // Skips are invisible to clients; journal only what they can see or must stop seeing.
        if (wasVisible || SuggestionDto.IsVisible(suggestion)) ContinuationTrigger.Record(unitOfWork, suggestion);
        await unitOfWork.CommitAsync(ct);
    }

    private async Task<(string? Title, SuggestionOrigin Origin, bool CalledLlm)> DecideAsync(Node node, CancellationToken ct)
    {
        var decision = ContinuationRules.Evaluate(node.Title);
        if (decision.Kind == ContinuationDecisionKind.Skip) return (null, SuggestionOrigin.Template, false);
        if (decision.Kind == ContinuationDecisionKind.Template) return (decision.Title, SuggestionOrigin.Template, false);

        var normalized = ContinuationRules.Normalize(node.Title);
        var cacheable = ContinuationRules.IsCacheable(node.Title, node.Description);
        var key = ContinuationRules.CacheKey(normalized, PromptVersion, secrets.Value.HashPepper);
        var now = time.GetUtcNow();
        if (cacheable && await db.ContinuationCache.SingleOrDefaultAsync(c => c.KeyHash == key, ct) is { } cached)
        {
            cached.Hits++;
            cached.LastUsedAt = now;
            return (cached.ResultTitle, SuggestionOrigin.Cache, false);
        }

        var calls = await db.NodeSuggestions.CountAsync(s => s.UserId == node.OwnerId && s.LlmCalledAt > now.AddDays(-1), ct);
        if (calls >= options.Value.MaxLlmCallsPerUserPerDay) return (null, SuggestionOrigin.Llm, false);

        string? title;
        try
        {
            var result = await llm.GenerateAsync(BuildRequest(node.Title, node.Description), ct);
            title = ParseTitle(result.Content, normalized);
        }
        catch (LlmException e)
        {
            logger.LogWarning("Continuation for node {NodeId} skipped: {Category}", node.Id, e.Category);
            return (null, SuggestionOrigin.Llm, true);
        }

        if (cacheable)
            db.ContinuationCache.Add(new ContinuationCacheEntry
            {
                KeyHash = key, PromptVersion = PromptVersion, ResultTitle = title, CreatedAt = now, LastUsedAt = now
            });
        return (title, SuggestionOrigin.Llm, true);
    }

    /// <summary>Hard filter: one-time, not deleted, not already done, not shared (MVP), content unchanged.</summary>
    private async Task<bool> StillCurrentAsync(Node node, string contentHash, CancellationToken ct)
    {
        var current = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == node.Id, ct);
        if (current is not { Type: NodeType.OneTime, DeletedAt: null }) return false;
        if (ContinuationRules.ContentHash(current.Title, current.Description) != contentHash) return false;
        var done = await db.NodeOccurrenceStates.AnyAsync(s => s.NodeId == node.Id && s.State == OccurrenceState.Untied, ct);
        var shared = await db.SharedNodeMembers.AnyAsync(m => m.NodeId == node.Id, ct);
        return !done && !shared;
    }

    internal static LlmRequest BuildRequest(string title, string? description) => new("continuation",
    [
        LlmMessage.System("""
            Ты помогаешь планировщику Nodus предложить следующий шаг после выполнения дела.
            Предлагай продолжение, только если оно очевидно следует из дела: «Записаться к врачу» → «Визит к врачу»,
            «Подать документы на визу» → «Получить визу». Если явного следующего шага нет — верни null.
            Не придумывай даты, имена, места и подробности, которых нет в исходном деле.
            Ответь строго JSON: {"continuation": {"title": "..."}} или {"continuation": null}.
            title — до 80 символов, на языке исходного дела.
            """),
        LlmMessage.User(description is null ? title : $"{title}\n{description}")
    ], Temperature: 0.1, MaxTokens: 120);

    /// <summary>Null for "no continuation", malformed output, or an echo of the source.</summary>
    internal static string? ParseTitle(string content, string normalizedSource)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("continuation", out var continuation) ||
                continuation.ValueKind != JsonValueKind.Object ||
                !continuation.TryGetProperty("title", out var titleElement) || titleElement.ValueKind != JsonValueKind.String)
                return null;
            var title = titleElement.GetString()!.Trim();
            return title.Length is >= 2 and <= 80 && ContinuationRules.Normalize(title) != normalizedSource ? title : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
