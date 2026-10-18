using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Observability;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Api.Plans;

public enum FeatureCheck
{
    Allowed,
    NotInPlan,
    ConsentRequired
}

/// <summary>
/// The only place that decides whether a user may use a plan feature. The stored plan applies until it expires;
/// AI features additionally need the user's consent. Results are cached per scope (request or job), so batch
/// operations do not query the user per item.
/// </summary>
public sealed class FeatureAccess(NodusDbContext db, IOptions<PlanOptions> options, TimeProvider time)
{
    public const string NotAvailableCode = "feature_not_available";
    public const string ConsentRequiredCode = "ai_consent_required";

    private readonly Dictionary<Guid, Grant?> _grants = [];

    private sealed record Grant(UserPlan Plan, DateTimeOffset? ExpiresAt, bool Consented);

    public UserPlan EffectivePlan(User user) => Effective(user.Plan, user.PlanExpiresAt);

    /// <summary>Features of the effective plan, regardless of consent: clients show entry points by this list.</summary>
    public IReadOnlySet<string> For(User user) => options.Value.FeaturesOf(EffectivePlan(user));

    public async Task<FeatureCheck> CheckAsync(Guid userId, string feature, CancellationToken ct)
    {
        if (!_grants.TryGetValue(userId, out var grant))
            _grants[userId] = grant = await db.Users.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => new Grant(u.Plan, u.PlanExpiresAt, u.AiConsentAt != null))
                .SingleOrDefaultAsync(ct);

        if (grant is null || !options.Value.FeaturesOf(Effective(grant.Plan, grant.ExpiresAt)).Contains(feature))
            return FeatureCheck.NotInPlan;
        return Features.RequiresAiConsent(feature) && !grant.Consented ? FeatureCheck.ConsentRequired : FeatureCheck.Allowed;
    }

    /// <summary><c>null</c> when allowed; otherwise the <c>403</c> to return. Only explicit requests count as denials.</summary>
    public async Task<ApiResult?> DenyAsync(Guid userId, string feature, CancellationToken ct)
    {
        var check = await CheckAsync(userId, feature, ct);
        if (check == FeatureCheck.Allowed) return null;

        var extensions = new Dictionary<string, object?> { ["feature"] = feature };
        if (check == FeatureCheck.NotInPlan)
        {
            NodusMetrics.FeatureDenied(feature, "not_in_plan");
            return ApiResult.Problem(StatusCodes.Status403Forbidden, NotAvailableCode, "Feature is not available.",
                extensions: extensions);
        }

        NodusMetrics.FeatureDenied(feature, "consent_required");
        return ApiResult.Problem(StatusCodes.Status403Forbidden, ConsentRequiredCode,
            "Consent to process data with AI is required.", extensions: extensions);
    }

    private UserPlan Effective(UserPlan plan, DateTimeOffset? expiresAt) =>
        plan != UserPlan.Standard && expiresAt is { } at && at <= time.GetUtcNow() ? UserPlan.Standard : plan;
}

/// <summary>Rejects the request before the idempotency filter, so a denial is not stored for the key.</summary>
public sealed class FeatureFilter(string feature) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var access = http.RequestServices.GetRequiredService<FeatureAccess>();
        return await access.DenyAsync(http.User.GetUserId(), feature, http.RequestAborted) ?? await next(context);
    }
}

public static class FeatureFilterExtensions
{
    /// <summary>Call before <c>WithIdempotency</c>: filters run in the order they are added.</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string feature) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new FeatureFilter(feature));
}
