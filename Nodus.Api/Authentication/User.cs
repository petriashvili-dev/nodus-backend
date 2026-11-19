using Nodus.Api.Plans;

namespace Nodus.Api.Authentication;

public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>True until an email is verified. Linking an email changes this flag, never the id.</summary>
    public bool IsAnonymous { get; set; } = true;

    /// <summary>Normalised (trimmed, lower-case) verified email.</summary>
    public string? Email { get; set; }

    public DateTimeOffset? EmailVerifiedAt { get; set; }

    /// <summary>The user's current IANA time zone as last reported by the device (<c>PUT /v1/me/timezone</c>).</summary>
    public string? Timezone { get; set; }

    /// <summary>Name shown to trusted connections and co-members of shared nodes.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Stored plan; it is in effect only until <see cref="PlanExpiresAt"/> (see <c>FeatureAccess</c>).</summary>
    public UserPlan Plan { get; set; }

    public PlanSource? PlanSource { get; set; }

    /// <summary><c>null</c> — the plan does not expire.</summary>
    public DateTimeOffset? PlanExpiresAt { get; set; }

    /// <summary>When the user agreed to send their content to AI providers; <c>null</c> — not agreed or withdrawn.</summary>
    public DateTimeOffset? AiConsentAt { get; set; }

    /// <summary>Last sync revision allocated to this user as a change recipient (see <c>SyncChange</c>).</summary>
    public long SyncRevision { get; set; }

    /// <summary>Changes up to this revision were pruned; older cursors require a full resync.</summary>
    public long SyncPrunedRevision { get; set; }

    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
