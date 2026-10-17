using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Api.Sync;

/// <summary>
/// Makes a mutation endpoint idempotent per user and <c>Idempotency-Key</c>:
/// the handler, its sync changes, outbox events and the stored response commit in one transaction;
/// a retry with the same key and the same request replays the stored response; the same key with a different
/// request is rejected with 422. Rejected (4xx) outcomes are stored too, so a retry gets the same answer.
/// </summary>
public sealed class IdempotencyFilter : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > Limits.IdempotencyKeyLength)
            return ApiResult.Problem(StatusCodes.Status400BadRequest, ProblemCodes.IdempotencyKeyRequired,
                $"Header {HeaderName} (1–{Limits.IdempotencyKeyLength} characters) is required for mutations.");

        var services = http.RequestServices;
        var db = services.GetRequiredService<NodusDbContext>();
        var unitOfWork = services.GetRequiredService<UnitOfWork>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var userId = http.User.GetUserId();
        var requestHash = await HashRequestAsync(http.Request);

        if (await ReplayAsync(db, http, userId, key, requestHash) is { } stored) return stored;

        ApiResult result;
        await using (var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted))
        {
            try
            {
                result = await next(context) as ApiResult
                         ?? throw new InvalidOperationException("Idempotent endpoints must return ApiResult.");
            }
            catch (DbUpdateConcurrencyException)
            {
                result = ApiResult.Problem(StatusCodes.Status409Conflict, ProblemCodes.VersionConflict,
                    "The entity was changed concurrently; sync and retry.");
            }
            catch (DbUpdateException e) when (e.IsUniqueViolation())
            {
                result = ApiResult.Problem(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
                    "The operation conflicts with existing data; sync and retry.");
            }

            if (!result.IsSuccess)
            {
                // Nothing of a rejected operation may persist except its recorded outcome.
                await transaction.RollbackAsync(http.RequestAborted);
                unitOfWork.Discard();
            }

            db.ClientOperations.Add(new ClientOperation
            {
                UserId = userId, IdempotencyKey = key, RequestHash = requestHash, StatusCode = result.StatusCode,
                ResponseBody = result.Value is null ? null : JsonSerializer.Serialize(result.Value, NodusJson.Options),
                CreatedAt = now
            });

            try
            {
                if (result.IsSuccess)
                {
                    await db.SaveChangesAsync(http.RequestAborted);
                    await transaction.CommitAsync(http.RequestAborted);
                }
                else
                {
                    await db.SaveChangesAsync(http.RequestAborted);
                }
            }
            catch (DbUpdateException e) when (e.IsUniqueViolation())
            {
                // A concurrent request with the same key won the race; its outcome is authoritative.
                if (result.IsSuccess) await transaction.RollbackAsync(CancellationToken.None);
                unitOfWork.Discard();
                return await ReplayAsync(db, http, userId, key, requestHash)
                       ?? throw new InvalidOperationException("Idempotency record vanished after a key conflict.");
            }
        }

        if (result.IsSuccess) unitOfWork.AfterCommit();
        return result;
    }

    private static async Task<ApiResult?> ReplayAsync(NodusDbContext db, HttpContext http, Guid userId, string key, string requestHash)
    {
        var existing = await db.ClientOperations.AsNoTracking()
            .SingleOrDefaultAsync(o => o.UserId == userId && o.IdempotencyKey == key, http.RequestAborted);
        if (existing is null) return null;
        if (existing.RequestHash != requestHash)
            return ApiResult.Problem(StatusCodes.Status422UnprocessableEntity, ProblemCodes.IdempotencyConflict,
                "This Idempotency-Key was already used for a different request.");

        http.Response.Headers[ReplayedHeaderName] = "true";
        return ApiResult.Stored(existing.StatusCode, existing.ResponseBody);
    }

    /// <summary>Method, path, query and the exact body bytes (buffered by <see cref="EnableRequestBuffering"/>).</summary>
    private static async Task<string> HashRequestAsync(HttpRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString}\n"));
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
                hash.AppendData(buffer, 0, read);
            request.Body.Position = 0;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Middleware: lets the filter re-read bodies that model binding has already consumed.</summary>
    public static IApplicationBuilder EnableRequestBuffering(IApplicationBuilder app) => app.Use((http, next) =>
    {
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
            http.Request.EnableBuffering(Limits.RequestBodyBytes);
        return next(http);
    });
}

public static class IdempotencyExtensions
{
    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, IdempotencyFilter>();
}
