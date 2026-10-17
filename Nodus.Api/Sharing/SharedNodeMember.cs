namespace Nodus.Api.Sharing;

/// <summary>A trusted user who shares a node with its owner; members edit and untie/retie on equal terms.</summary>
public sealed class SharedNodeMember
{
    public Guid NodeId { get; set; }
    public Guid UserId { get; set; }
    public Guid AddedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}
