using Microsoft.AspNetCore.Mvc;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Sync;

namespace Nodus.Api.DataPortability;

public static class DataPortabilityEndpoints
{
    public static void MapDataPortabilityEndpoints(this RouteGroupBuilder v1)
    {
        var me = v1.MapGroup("/me").WithTags("Export / import");

        me.MapGet("/export", async (HttpContext http, ExportService export, TimeProvider time, CancellationToken ct) =>
        {
            var document = await export.ExportAsync(http.User.GetUserId(), ct);
            http.Response.Headers.ContentDisposition =
                $"attachment; filename=\"nodus-export-{time.GetUtcNow():yyyy-MM-dd}.json\"";
            return ApiResult.Ok(document);
        });

        me.MapPost("/import", (ImportRequest request, HttpContext http, ImportService imports, CancellationToken ct) =>
                imports.ImportAsync(http.User.GetUserId(), request, ct))
            .WithIdempotency().RequireRateLimiting(RateLimits.Ai)
            .WithMetadata(new RequestSizeLimitAttribute(Limits.ImportBodyBytes));

        me.MapGet("/imports/{id:guid}", (Guid id, HttpContext http, ImportService imports, CancellationToken ct) =>
            imports.GetAsync(http.User.GetUserId(), id, ct));

        me.MapPost("/imports/{id:guid}/confirm", (Guid id, ConfirmImportRequest? request, HttpContext http,
            ImportService imports, CancellationToken ct) => imports.ConfirmAsync(http.User.GetUserId(), id, request, ct))
            .WithIdempotency();

        me.MapPost("/imports/{id:guid}/cancel", (Guid id, HttpContext http, ImportService imports, CancellationToken ct) =>
            imports.CancelAsync(http.User.GetUserId(), id, ct)).WithIdempotency();
    }
}
