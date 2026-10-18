using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Nodus.Api.Common;
using Nodus.Api.Connections;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;
using Nodus.Api.Sync;

namespace Nodus.Api.Authentication;

/// <summary>
/// Irreversible deletion of an account (App Store guideline 5.1.1(v)). Everything owned by the user is removed by
/// database cascades from <c>users</c>; before that, the other people affected are journaled so their devices drop
/// what they can no longer see: members of the user's shared nodes get tombstones, owners of nodes the user was a
/// member of get the updated member list, connections get tombstones.
/// </summary>
public sealed class AccountDeletionService(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    NodeAccess access,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<AccountDeletionService> logger)
{
    public async Task<ApiResult> DeleteAsync(Guid userId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) return ApiResult.NoContent();

        // Shared nodes the user owns: every member loses the node.
        var ownedShared = await db.SharedNodeMembers
            .Join(db.Nodes, m => m.NodeId, n => n.Id, (m, n) => new { m.NodeId, m.UserId, n.OwnerId, n.Version })
            .Where(x => x.OwnerId == userId)
            .ToListAsync(ct);
        foreach (var x in ownedShared)
            unitOfWork.Journal.Record(SyncEntityType.Node, x.NodeId.ToString(), x.Version + 1, SyncOperation.Delete, [x.UserId]);

        // Nodes of other owners the user was a member of: the remaining participants see the member list change.
        var memberOf = await db.Nodes
            .Where(n => n.OwnerId != userId && db.SharedNodeMembers.Any(m => m.NodeId == n.Id && m.UserId == userId))
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        var members = await access.MembersAsync(memberOf.Select(n => n.Id).ToArray(), ct);
        foreach (var node in memberOf)
        {
            node.Version++;
            node.UpdatedAt = now;
            unitOfWork.Journal.Record(SyncEntityType.Node, node.Id.ToString(), node.Version, SyncOperation.Upsert,
                [node.OwnerId, .. members[node.Id].Where(m => m != userId)]);
        }

        var connections = await db.Connections
            .Where(c => (c.UserLowId == userId || c.UserHighId == userId) && c.Status != ConnectionStatus.Removed)
            .ToListAsync(ct);
        foreach (var connection in connections)
            unitOfWork.Journal.Record(SyncEntityType.Connection, connection.Id.ToString(), connection.Version + 1,
                SyncOperation.Delete, [connection.Other(userId)]);

        var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).SingleAsync(ct);
        await unitOfWork.CommitAsync(ct); // journal for others + member-list version bumps

        // Cascades remove tokens, devices, nodes with revisions/states/members/suggestions, connections, invites,
        // AI jobs with drafts, sync feed and idempotency records of the user.
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
        if (email is not null) await db.EmailVerificationCodes.Where(c => c.Email == email).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        unitOfWork.AfterCommit();

        // Access tokens stay cryptographically valid until they expire; the existence check rejects them at once.
        cache.Remove(AuthenticationSetup.UserExistsCacheKey(userId));
        logger.LogInformation("Account {UserId} deleted", userId);
        return ApiResult.NoContent();
    }
}
