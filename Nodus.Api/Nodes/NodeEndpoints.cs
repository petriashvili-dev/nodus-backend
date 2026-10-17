using Microsoft.AspNetCore.Mvc;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Occurrences;
using Nodus.Api.Sync;

namespace Nodus.Api.Nodes;

public static class NodeEndpoints
{
    public static void MapNodeEndpoints(this RouteGroupBuilder v1)
    {
        var nodes = v1.MapGroup("/nodes").WithTags("Nodes");

        nodes.MapPost("/", (CreateNodeRequest request, HttpContext http, NodeService service, CancellationToken ct) =>
            service.CreateAsync(http.User.GetUserId(), request, ct)).WithIdempotency();

        nodes.MapPost("/batch", (BatchCreateNodesRequest request, HttpContext http, NodeService service, CancellationToken ct) =>
            service.CreateBatchAsync(http.User.GetUserId(), request, ct)).WithIdempotency();

        nodes.MapGet("/{id:guid}", async (Guid id, HttpContext http, NodeAccess access, NodeHistoryLoader histories,
            NodeDtoFactory dtos, CancellationToken ct) =>
            await access.FindAsync(http.User.GetUserId(), id, ct) is { DeletedAt: null } node
                ? ApiResult.Ok(dtos.Create(await histories.LoadAsync(node, ct)))
                : ApiResult.NotFound("Node"));

        nodes.MapPatch("/{id:guid}", (Guid id, UpdateNodeRequest request, HttpContext http, NodeService service,
            CancellationToken ct) => service.UpdateAsync(http.User.GetUserId(), id, request, ct)).WithIdempotency();

        nodes.MapDelete("/{id:guid}", (Guid id, [FromQuery(Name = "expected_version")] long? expectedVersion,
                HttpContext http, NodeService service, CancellationToken ct) =>
            service.DeleteAsync(http.User.GetUserId(), id, expectedVersion, ct)).WithIdempotency();

        nodes.MapPut("/{id:guid}/occurrences/{key}/untie", (Guid id, string key, OccurrenceActionRequest? request,
                HttpContext http, NodeService service, CancellationToken ct) =>
            service.SetOccurrenceStateAsync(http.User.GetUserId(), id, key, OccurrenceState.Untied, request?.CompletedAt, ct))
            .WithIdempotency();

        nodes.MapPut("/{id:guid}/occurrences/{key}/retie", (Guid id, string key, HttpContext http, NodeService service,
                CancellationToken ct) =>
            service.SetOccurrenceStateAsync(http.User.GetUserId(), id, key, OccurrenceState.Tied, null, ct))
            .WithIdempotency();
    }
}
