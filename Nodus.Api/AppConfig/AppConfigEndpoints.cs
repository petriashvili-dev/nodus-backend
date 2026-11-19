namespace Nodus.Api.AppConfig;

public static class AppConfigEndpoints
{
    /// <summary>
    /// Public: the client needs links and the minimum version before it signs in. <c>no-cache</c> lets it keep the
    /// response but revalidate with <c>If-None-Match</c> each time, which costs a <c>304</c> when nothing changed.
    /// </summary>
    public static void MapAppConfigEndpoints(this RouteGroupBuilder v1) =>
        v1.MapGet("/app-config", async (HttpContext http, AppConfigService config, CancellationToken ct) =>
        {
            var document = await config.GetDocumentAsync(ct);
            http.Response.Headers.ETag = document.ETag;
            http.Response.Headers.CacheControl = "no-cache";
            return http.Request.Headers.IfNoneMatch.Any(tag => tag == document.ETag)
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Text(document.Json, "application/json; charset=utf-8");
        }).AllowAnonymous().WithTags("App config");
}
