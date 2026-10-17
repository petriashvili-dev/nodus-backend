using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Notifications;
using Nodus.Api.Sharing;
using Nodus.Api.Sync;

namespace Nodus.Api.Connections;

public sealed class ConnectionOptions
{
    public const string Section = "Connections";

    public int InviteLifetimeHours { get; init; } = 24;
    public int MaxPendingInvites { get; init; } = 10;

    /// <summary>Base of the shareable invitation link; the token is appended.</summary>
    public string InviteLinkBase { get; init; } = "https://nodus.app/invite/";
}

public sealed record ConnectionDto(
    Guid Id,
    Guid UserId,
    string? DisplayName,
    ConnectionStatus Status,
    bool InvitedByMe,
    bool AwaitingMyConfirmation,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt);

public sealed record CreatedInviteDto(Guid Id, string Token, string Code, string Link, DateTimeOffset ExpiresAt);

public sealed record AcceptInviteRequest(string? Code);

public sealed class ConnectionService(
    NodusDbContext db,
    UnitOfWork unitOfWork,
    SharingService sharing,
    PushNotifier push,
    IOptions<ConnectionOptions> options,
    IOptions<EmailOtpOptions> secrets,
    TimeProvider time)
{
    // Crockford base32 without look-alike characters: 32^8 ≈ 10^12 codes.
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int CodeLength = 8;

    public async Task<ApiResult> CreateInviteAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var pending = await db.ConnectionInvites.CountAsync(i => i.InviterId == userId && i.Status == InviteStatus.Pending &&
                                                                 i.ExpiresAt > now, ct);
        if (pending >= options.Value.MaxPendingInvites)
            return ApiResult.Problem(StatusCodes.Status409Conflict, "too_many_invites",
                "Too many pending invitations; revoke one or wait until they expire.");

        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var code = RandomNumberGenerator.GetString(CodeAlphabet, CodeLength);
        var invite = new ConnectionInvite
        {
            Id = Guid.NewGuid(), InviterId = userId, TokenHash = HashToken(token), CodeHash = HashCode(code),
            Status = InviteStatus.Pending, CreatedAt = now, ExpiresAt = now.AddHours(options.Value.InviteLifetimeHours)
        };
        db.ConnectionInvites.Add(invite);
        await unitOfWork.CommitAsync(ct);
        return ApiResult.Created(new CreatedInviteDto(invite.Id, token, code, options.Value.InviteLinkBase + token, invite.ExpiresAt));
    }

    public async Task<ApiResult> RevokeInviteAsync(Guid userId, Guid inviteId, CancellationToken ct)
    {
        await db.ConnectionInvites.Where(i => i.Id == inviteId && i.InviterId == userId && i.Status == InviteStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, InviteStatus.Revoked), ct);
        return ApiResult.NoContent();
    }

    /// <summary>
    /// Accepts by link token or by code. Invalid, used, revoked and expired invitations are indistinguishable.
    /// Accepting the counterpart's invitation while one's own invitation to them is pending confirmation means both
    /// sides consented, so the connection becomes active at once.
    /// </summary>
    public async Task<ApiResult> AcceptAsync(Guid userId, string? token, string? code, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var hash = token is not null ? HashToken(token) : HashCode(NormalizeCode(code));
        var invite = await (token is not null
                ? db.ConnectionInvites.FromSql(
                    $"SELECT * FROM connection_invites WHERE token_hash = {hash} AND status = 'Pending' LIMIT 1 FOR UPDATE")
                : db.ConnectionInvites.FromSql(
                    $"SELECT * FROM connection_invites WHERE code_hash = {hash} AND status = 'Pending' LIMIT 1 FOR UPDATE"))
            .FirstOrDefaultAsync(ct);
        if (invite is null || invite.Status != InviteStatus.Pending || invite.ExpiresAt <= now)
            return ApiResult.Problem(StatusCodes.Status404NotFound, "invite_invalid", "The invitation is invalid or has expired.");
        if (invite.InviterId == userId)
            return ApiResult.Problem(StatusCodes.Status400BadRequest, "own_invite", "You cannot accept your own invitation.");

        var (low, high) = Connection.Normalize(userId, invite.InviterId);
        var connection = await db.Connections
            .FromSql($"SELECT * FROM connections WHERE user_low_id = {low} AND user_high_id = {high} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (connection?.Status == ConnectionStatus.Active)
            return ApiResult.Problem(StatusCodes.Status409Conflict, "already_connected", "You are already connected.");

        if (connection is null)
        {
            connection = new Connection { Id = Guid.NewGuid(), UserLowId = low, UserHighId = high, CreatedAt = now };
            db.Connections.Add(connection);
        }

        if (connection.Status == ConnectionStatus.Pending && connection.InviterId == userId)
        {
            connection.Status = ConnectionStatus.Active;
            connection.ConfirmedAt = now;
        }
        else
        {
            connection.Status = ConnectionStatus.Pending;
            connection.InviterId = invite.InviterId;
            connection.ConfirmedAt = null;
        }

        connection.RemovedAt = null;
        connection.UpdatedAt = now;
        connection.Version++;
        invite.Status = InviteStatus.Accepted;
        invite.AcceptedBy = userId;
        invite.AcceptedAt = now;
        Record(connection);
        push.Request(new PushRequest(invite.InviterId, $"connection-{connection.Id}-{connection.Version}",
            PushKinds.ConnectionChanged,
            connection.Status == ConnectionStatus.Active ? "Связь установлена" : "Приглашение принято",
            connection.Status == ConnectionStatus.Active ? "Теперь вы можете делиться узлами." : "Подтвердите связь в Nodus.",
            new Dictionary<string, string> { ["connection_id"] = connection.Id.ToString() }));

        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(await DtoAsync(connection, userId, ct));
    }

    public async Task<ApiResult> ConfirmAsync(Guid userId, Guid connectionId, CancellationToken ct)
    {
        var connection = await db.Connections.SingleOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null || !connection.Involves(userId) || connection.Status == ConnectionStatus.Removed)
            return ApiResult.NotFound("Connection");
        if (connection.Status == ConnectionStatus.Active) return ApiResult.Ok(await DtoAsync(connection, userId, ct));
        if (connection.InviterId != userId)
            return ApiResult.Problem(StatusCodes.Status403Forbidden, "forbidden", "Only the inviter confirms the connection.");

        var now = time.GetUtcNow();
        connection.Status = ConnectionStatus.Active;
        connection.ConfirmedAt = now;
        connection.UpdatedAt = now;
        connection.Version++;
        Record(connection);
        push.Request(new PushRequest(connection.Other(userId), $"connection-{connection.Id}-{connection.Version}",
            PushKinds.ConnectionChanged, "Связь подтверждена", "Теперь вы можете делиться узлами.",
            new Dictionary<string, string> { ["connection_id"] = connection.Id.ToString() }));

        await unitOfWork.CommitAsync(ct);
        return ApiResult.Ok(await DtoAsync(connection, userId, ct));
    }

    /// <summary>Declines a pending or ends an active connection; nodes shared between the two stop being shared.</summary>
    public async Task<ApiResult> RemoveAsync(Guid userId, Guid connectionId, CancellationToken ct)
    {
        var connection = await db.Connections.SingleOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null || !connection.Involves(userId)) return ApiResult.NotFound("Connection");
        if (connection.Status == ConnectionStatus.Removed) return ApiResult.NoContent();

        var now = time.GetUtcNow();
        connection.Status = ConnectionStatus.Removed;
        connection.RemovedAt = now;
        connection.UpdatedAt = now;
        connection.Version++;
        Record(connection);
        await sharing.UnshareBetweenAsync(connection.UserLowId, connection.UserHighId, userId, ct);

        await unitOfWork.CommitAsync(ct);
        return ApiResult.NoContent();
    }

    public async Task<IReadOnlyList<ConnectionDto>> ListAsync(Guid userId, CancellationToken ct)
    {
        var connections = await db.Connections.AsNoTracking()
            .Where(c => (c.UserLowId == userId || c.UserHighId == userId) && c.Status != ConnectionStatus.Removed)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return await DtosAsync(connections, userId, ct);
    }

    public async Task<IReadOnlyList<ConnectionDto>> DtosAsync(IReadOnlyCollection<Connection> connections, Guid userId,
        CancellationToken ct)
    {
        var others = connections.Select(c => c.Other(userId)).ToArray();
        var names = await db.Users.AsNoTracking().Where(u => others.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return connections.Select(c => new ConnectionDto(c.Id, c.Other(userId), names.GetValueOrDefault(c.Other(userId)), c.Status,
            c.InviterId == userId, c.Status == ConnectionStatus.Pending && c.InviterId == userId, c.Version, c.CreatedAt,
            c.ConfirmedAt)).ToList();
    }

    private async Task<ConnectionDto> DtoAsync(Connection connection, Guid userId, CancellationToken ct) =>
        (await DtosAsync([connection], userId, ct))[0];

    private void Record(Connection connection) =>
        unitOfWork.Journal.Record(SyncEntityType.Connection, connection.Id.ToString(), connection.Version, SyncOperation.Upsert,
            [connection.UserLowId, connection.UserHighId]);

    private static string NormalizeCode(string? code) =>
        new string((code ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray())
            .Replace('O', '0').Replace('I', '1').Replace('L', '1');

    private static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Codes are short, so they are hashed with the server pepper to resist offline guessing.</summary>
    private string HashCode(string code) => Convert.ToHexStringLower(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(secrets.Value.HashPepper), Encoding.UTF8.GetBytes($"invite:{code}")));
}
