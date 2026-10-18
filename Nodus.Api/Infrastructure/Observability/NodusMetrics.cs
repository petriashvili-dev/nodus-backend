using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Nodus.Api.Infrastructure.Observability;

/// <summary>Application metrics. Tags carry categories only — never node content, prompts or user ids.</summary>
public static class NodusMetrics
{
    public const string MeterName = "Nodus.Api";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> LlmRequests = Meter.CreateCounter<long>("nodus.llm.requests", description: "LLM attempts by provider and outcome");
    private static readonly Histogram<double> LlmLatency = Meter.CreateHistogram<double>("nodus.llm.latency", "ms");
    private static readonly Counter<long> LlmTokens = Meter.CreateCounter<long>("nodus.llm.tokens");
    private static readonly Counter<double> LlmCost = Meter.CreateCounter<double>("nodus.llm.cost", description: "Estimated LLM cost in billing currency");
    private static readonly Counter<long> OutboxEvents = Meter.CreateCounter<long>("nodus.outbox.events");
    private static readonly Histogram<double> OutboxLag = Meter.CreateHistogram<double>("nodus.outbox.lag", "s", "Commit to dispatch delay");
    private static readonly Counter<long> IdempotentReplays = Meter.CreateCounter<long>("nodus.idempotency.replays");
    private static readonly Counter<long> SyncRequests = Meter.CreateCounter<long>("nodus.sync.requests");
    private static readonly Counter<long> Pushes = Meter.CreateCounter<long>("nodus.push.deliveries");
    private static readonly Counter<long> AiJobs = Meter.CreateCounter<long>("nodus.ai.jobs", description: "AI job outcomes by kind");
    private static readonly Counter<long> Suggestions = Meter.CreateCounter<long>("nodus.continuations", description: "Continuation pipeline outcomes");
    private static readonly Counter<long> FeatureDenials = Meter.CreateCounter<long>("nodus.feature.denied", description: "Requests for plan features the user may not use");

    public static void LlmAttempt(string provider, string purpose, string outcome, TimeSpan latency)
    {
        var tags = new TagList { { "provider", provider }, { "purpose", purpose }, { "outcome", outcome } };
        LlmRequests.Add(1, tags);
        LlmLatency.Record(latency.TotalMilliseconds, tags);
    }

    public static void LlmUsage(string provider, int? promptTokens, int? completionTokens, decimal? cost)
    {
        if (promptTokens is { } p) LlmTokens.Add(p, new TagList { { "provider", provider }, { "kind", "prompt" } });
        if (completionTokens is { } c) LlmTokens.Add(c, new TagList { { "provider", provider }, { "kind", "completion" } });
        if (cost is { } value) LlmCost.Add((double)value, new TagList { { "provider", provider } });
    }

    public static void OutboxDispatched(string type, string outcome, TimeSpan lag)
    {
        OutboxEvents.Add(1, new TagList { { "type", type }, { "outcome", outcome } });
        if (outcome == "processed") OutboxLag.Record(Math.Max(0, lag.TotalSeconds), new TagList { { "type", type } });
    }

    public static void IdempotentReplay() => IdempotentReplays.Add(1);

    public static void Sync(string kind) => SyncRequests.Add(1, new TagList { { "kind", kind } });

    public static void Push(string outcome) => Pushes.Add(1, new TagList { { "outcome", outcome } });

    /// <summary>Outcome is <c>ready</c>, <c>retry</c>, <c>discarded</c> or an error code (<c>nothing_recognized</c>, <c>ai_unavailable</c>…).</summary>
    public static void AiJob(string kind, string outcome) => AiJobs.Add(1, new TagList { { "kind", kind }, { "outcome", outcome } });

    public static void Continuation(string outcome) => Suggestions.Add(1, new TagList { { "outcome", outcome } });

    /// <summary>Reason is <c>not_in_plan</c> or <c>consent_required</c>.</summary>
    public static void FeatureDenied(string feature, string reason) =>
        FeatureDenials.Add(1, new TagList { { "feature", feature }, { "reason", reason } });
}
