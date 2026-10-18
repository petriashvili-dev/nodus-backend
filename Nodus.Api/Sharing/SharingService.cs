using Microsoft.EntityFrameworkCore;
using Nodus.Api.Common;
using Nodus.Api.Connections;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;
using Nodus.Api.Notifications;
using Nodus.Api.Sync;

namespace Nodus.Api.Sharing;

public sealed record AddMemberRequest(Guid? UserId);

/// <summary>
/// Membership policy: only the owner adds or removes members (and deletes the node); a member may leave;
/// sharing requires an active trusted connection between owner and member.
/// Membership is part of the node DTO, so every change bumps the node version and is journaled for all
/// participants, with a tombstone for whoever lost access.
/// </summary>
public sealed class SharingService(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    NodeAccess access,
    NodeHistoryLoader histories,
    NodeDtoFactory dtos,
    PushNotifier push,
    TimeProvider time)
{
    public async Task<ApiResult> AddMemberAsync(Guid callerId, Guid nodeId, Guid? memberId, CancellationToken ct)
    {
        if (memberId is not { } userId || userId == Guid.Empty)
            return new ValidationErrors().Add("user_id", "The user to share with is required.").ToResult();
        var node = await access.FindAsync(callerId, nodeId, ct);
        if (node is null || node.DeletedAt is not null) return ApiResult.NotFound("Node");
        if (node.OwnerId != callerId)
            return ApiResult.Problem(StatusCodes.Status403Forbidden, "forbidden", "Only the owner manages members.");
        if (userId == callerId)
            return new ValidationErrors().Add("user_id", "The owner is already a participant.").ToResult();

        var (low, high) = Connection.Normalize(callerId, userId);
        if (!await db.Connections.AnyAsync(c => c.UserLowId == low && c.UserHighId == high && c.Status == ConnectionStatus.Active, ct))
            return ApiResult.Problem(StatusCodes.Status409Conflict, "not_connected",
                "Nodes can be shared only with confirmed trusted connections.");

        if (!await db.SharedNodeMembers.AnyAsync(m => m.NodeId == nodeId && m.UserId == userId, ct))
        {
            var now = time.GetUtcNow();
            db.SharedNodeMembers.Add(new SharedNodeMember { NodeId = nodeId, UserId = userId, AddedBy = callerId, AddedAt = now });
            Touch(node, callerId, now);
            var recipients = (await access.RecipientsAsync(node, ct)).Append(userId);
            unitOfWork.Journal.Record(SyncEntityType.Node, node.Id.ToString(), node.Version, SyncOperation.Upsert, recipients);
            // Lock-screen safe: no title of the node.
            push.Request(new PushRequest(userId, $"shared-{node.Id}-{node.Version}", PushKinds.SharedNodeChanged,
                "Новый совместный узел", "С вами поделились узлом в Nodus.",
                new Dictionary<string, string> { ["node_id"] = node.Id.ToString() }));
            await unitOfWork.CommitAsync(ct);
        }

        return ApiResult.Ok(dtos.Create(await histories.LoadAsync(node, ct)));
    }

    /// <summary>The owner removes anyone; a member removes only themselves (leaves).</summary>
    public async Task<ApiResult> RemoveMemberAsync(Guid callerId, Guid nodeId, Guid memberId, CancellationToken ct)
    {
        var node = await access.FindAsync(callerId, nodeId, ct);
        if (node is null || node.DeletedAt is not null) return ApiResult.NotFound("Node");
        if (node.OwnerId != callerId && memberId != callerId)
            return ApiResult.Problem(StatusCodes.Status403Forbidden, "forbidden", "Only the owner removes other members.");

        var member = await db.SharedNodeMembers.SingleOrDefaultAsync(m => m.NodeId == nodeId && m.UserId == memberId, ct);
        if (member is null) return ApiResult.NoContent();

        db.SharedNodeMembers.Remove(member);
        await RecordRemovalAsync(node, memberId, callerId, ct);
        await unitOfWork.CommitAsync(ct);
        return ApiResult.NoContent();
    }

    /// <summary>Used when a connection ends: nodes either of the two owns stop being shared with the other.</summary>
    public async Task UnshareBetweenAsync(Guid a, Guid b, Guid actorId, CancellationToken ct)
    {
        var memberships = await db.SharedNodeMembers
            .Join(db.Nodes, m => m.NodeId, n => n.Id, (m, n) => new { Member = m, Node = n })
            .Where(x => (x.Node.OwnerId == a && x.Member.UserId == b) || (x.Node.OwnerId == b && x.Member.UserId == a))
            .ToListAsync(ct);
        var members = await access.MembersAsync(memberships.Select(x => x.Node.Id).Distinct().ToArray(), ct);
        foreach (var x in memberships)
        {
            db.SharedNodeMembers.Remove(x.Member);
            RecordRemoval(x.Node, x.Member.UserId, actorId, [x.Node.OwnerId, .. members[x.Node.Id]]);
        }
    }

    private async Task RecordRemovalAsync(Node node, Guid removedUserId, Guid actorId, CancellationToken ct) =>
        RecordRemoval(node, removedUserId, actorId, await access.RecipientsAsync(node, ct));

    private void RecordRemoval(Node node, Guid removedUserId, Guid actorId, IEnumerable<Guid> recipients)
    {
        Touch(node, actorId, time.GetUtcNow());
        var remaining = recipients.Where(r => r != removedUserId);
        unitOfWork.Journal.Record(SyncEntityType.Node, node.Id.ToString(), node.Version, SyncOperation.Upsert, remaining);
        unitOfWork.Journal.Record(SyncEntityType.Node, node.Id.ToString(), node.Version, SyncOperation.Delete, [removedUserId]);
    }

    private static void Touch(Node node, Guid userId, DateTimeOffset now)
    {
        node.Version++;
        node.UpdatedAt = now;
        node.UpdatedBy = userId;
    }
}
