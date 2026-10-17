namespace Nodus.Api.Connections;

public enum ConnectionStatus
{
    /// <summary>The invitee accepted; the inviter still has to confirm.</summary>
    Pending,
    Active,
    Removed
}

/// <summary>
/// The single canonical trust relation of a pair of users. The pair is normalised (<see cref="UserLowId"/> &lt;
/// <see cref="UserHighId"/>) and unique, so crossing invitations and races cannot create two relations.
/// </summary>
public sealed class Connection
{
    public Guid Id { get; set; }
    public Guid UserLowId { get; set; }
    public Guid UserHighId { get; set; }
    public ConnectionStatus Status { get; set; }

    /// <summary>Author of the accepted invitation; the one who confirms a pending connection.</summary>
    public Guid InviterId { get; set; }

    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }

    public static (Guid Low, Guid High) Normalize(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);

    public bool Involves(Guid userId) => UserLowId == userId || UserHighId == userId;

    public Guid Other(Guid userId) => UserLowId == userId ? UserHighId : UserLowId;
}

public enum InviteStatus
{
    Pending,
    Accepted,
    Revoked
}

/// <summary>
/// One-time invitation, usable either as a link token (256 bits) or as a short code typed on the other phone.
/// Only hashes of both secrets are stored.
/// </summary>
public sealed class ConnectionInvite
{
    public Guid Id { get; set; }
    public Guid InviterId { get; set; }
    public required string TokenHash { get; set; }
    public required string CodeHash { get; set; }
    public InviteStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? AcceptedBy { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
}
