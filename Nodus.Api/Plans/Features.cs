namespace Nodus.Api.Plans;

/// <summary>Keys of features that depend on the user's plan; stable API values sent to clients.</summary>
public static class Features
{
    public const string AiDrafts = "ai_drafts";
    public const string AiContinuations = "ai_continuations";
    public const string AiImport = "ai_import";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([AiDrafts, AiContinuations, AiImport]);

    /// <summary>Every current feature sends user content to an AI provider, so all of them need the user's consent.</summary>
    public static bool RequiresAiConsent(string feature) => All.Contains(feature);
}

public enum UserPlan
{
    Standard,
    Plus
}

/// <summary>Who set the current plan; App Store purchases will add their own source.</summary>
public enum PlanSource
{
    Manual
}

public sealed class PlanOptions
{
    public const string Section = "Plans";

    public PlanFeatures Standard { get; init; } = new();
    public PlanFeatures Plus { get; init; } = new();

    /// <summary>
    /// <c>null</c> means "not configured": the binder appends configured arrays to initialised ones, so defaults
    /// live here instead of in the property initialiser.
    /// </summary>
    public IReadOnlySet<string> FeaturesOf(UserPlan plan) => plan switch
    {
        UserPlan.Plus => new HashSet<string>(Plus.Features ?? [.. Features.All]),
        _ => new HashSet<string>(Standard.Features ?? [])
    };

    public bool HasOnlyKnownFeatures() =>
        (Standard.Features ?? []).Concat(Plus.Features ?? []).All(Features.All.Contains);
}

public sealed class PlanFeatures
{
    public string[]? Features { get; init; }
}
