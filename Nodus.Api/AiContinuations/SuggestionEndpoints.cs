using Nodus.Api.Authentication;
using Nodus.Api.Sync;

namespace Nodus.Api.AiContinuations;

public static class SuggestionEndpoints
{
    public static void MapSuggestionEndpoints(this RouteGroupBuilder v1)
    {
        var suggestions = v1.MapGroup("/suggestions").WithTags("AI continuations");

        suggestions.MapPost("/{id:guid}/shown", (Guid id, HttpContext http, SuggestionService service, CancellationToken ct) =>
            service.MarkShownAsync(http.User.GetUserId(), id, ct)).WithIdempotency();

        suggestions.MapPost("/{id:guid}/accept", (Guid id, AcceptSuggestionRequest request, HttpContext http,
            SuggestionService service, CancellationToken ct) => service.AcceptAsync(http.User.GetUserId(), id, request, ct))
            .WithIdempotency();

        suggestions.MapPost("/{id:guid}/dismiss", (Guid id, HttpContext http, SuggestionService service, CancellationToken ct) =>
            service.DismissAsync(http.User.GetUserId(), id, ct)).WithIdempotency();
    }
}
