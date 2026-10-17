using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Sharing;
using Nodus.Api.Sync;

namespace Nodus.Api.Connections;

public static class ConnectionEndpoints
{
    public static void MapConnectionEndpoints(this RouteGroupBuilder v1)
    {
        var connections = v1.MapGroup("/connections").WithTags("Connections");

        connections.MapGet("/", async (HttpContext http, ConnectionService service, CancellationToken ct) =>
            ApiResult.Ok(new { connections = await service.ListAsync(http.User.GetUserId(), ct) }));

        connections.MapPost("/invites", (HttpContext http, ConnectionService service, CancellationToken ct) =>
                service.CreateInviteAsync(http.User.GetUserId(), ct))
            .WithIdempotency().RequireRateLimiting(RateLimits.Otp);

        connections.MapDelete("/invites/{id:guid}", (Guid id, HttpContext http, ConnectionService service, CancellationToken ct) =>
            service.RevokeInviteAsync(http.User.GetUserId(), id, ct));

        // Link flow: the token comes from the invitation URL.
        connections.MapPost("/invites/{token}/accept", (string token, HttpContext http, ConnectionService service,
                CancellationToken ct) => service.AcceptAsync(http.User.GetUserId(), token, null, ct))
            .WithIdempotency().RequireRateLimiting(RateLimits.Otp);

        // OTP flow: the short code is typed on the other phone.
        connections.MapPost("/invites/accept", (AcceptInviteRequest request, HttpContext http, ConnectionService service,
                CancellationToken ct) => string.IsNullOrWhiteSpace(request.Code)
                ? Task.FromResult(new ValidationErrors().Add("code", "The invitation code is required.").ToResult())
                : service.AcceptAsync(http.User.GetUserId(), null, request.Code, ct))
            .WithIdempotency().RequireRateLimiting(RateLimits.Otp);

        connections.MapPost("/{id:guid}/confirm", (Guid id, HttpContext http, ConnectionService service, CancellationToken ct) =>
            service.ConfirmAsync(http.User.GetUserId(), id, ct)).WithIdempotency();

        connections.MapDelete("/{id:guid}", (Guid id, HttpContext http, ConnectionService service, CancellationToken ct) =>
            service.RemoveAsync(http.User.GetUserId(), id, ct)).WithIdempotency();

        var members = v1.MapGroup("/nodes/{nodeId:guid}/members").WithTags("Sharing");

        members.MapPost("/", (Guid nodeId, AddMemberRequest request, HttpContext http, SharingService service,
            CancellationToken ct) => service.AddMemberAsync(http.User.GetUserId(), nodeId, request.UserId, ct)).WithIdempotency();

        members.MapDelete("/{userId:guid}", (Guid nodeId, Guid userId, HttpContext http, SharingService service,
            CancellationToken ct) => service.RemoveMemberAsync(http.User.GetUserId(), nodeId, userId, ct)).WithIdempotency();
    }
}
