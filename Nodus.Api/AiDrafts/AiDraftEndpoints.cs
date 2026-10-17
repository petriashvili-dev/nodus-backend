using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Sync;

namespace Nodus.Api.AiDrafts;

public static class AiDraftEndpoints
{
    public static void MapAiDraftEndpoints(this RouteGroupBuilder v1)
    {
        var drafts = v1.MapGroup("/ai/node-drafts").WithTags("AI drafts");

        drafts.MapPost("/", (CreateDraftJobRequest request, HttpContext http, AiDraftService service, CancellationToken ct) =>
                service.CreateAsync(http.User.GetUserId(), request, ct))
            .WithIdempotency().RequireRateLimiting(RateLimits.Ai);

        drafts.MapGet("/", (HttpContext http, AiDraftService service, CancellationToken ct) =>
            service.ListOpenAsync(http.User.GetUserId(), ct));

        drafts.MapGet("/{jobId:guid}", (Guid jobId, HttpContext http, AiDraftService service, CancellationToken ct) =>
            service.GetAsync(http.User.GetUserId(), jobId, ct));

        drafts.MapPost("/{jobId:guid}/confirm", (Guid jobId, ConfirmDraftJobRequest? request, HttpContext http,
            AiDraftService service, CancellationToken ct) => service.ConfirmAsync(http.User.GetUserId(), jobId, request, ct))
            .WithIdempotency();

        drafts.MapPost("/{jobId:guid}/cancel", (Guid jobId, HttpContext http, AiDraftService service, CancellationToken ct) =>
            service.CancelAsync(http.User.GetUserId(), jobId, ct)).WithIdempotency();
    }
}
