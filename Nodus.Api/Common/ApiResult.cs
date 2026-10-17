using System.Text.Json;

namespace Nodus.Api.Common;

/// <summary>
/// JSON result with an explicit status code and a serialisable value. Mutation handlers return it so that the
/// idempotency filter can persist and replay exactly the same response.
/// Errors use RFC 9457 problem details with a stable machine-readable <c>code</c>.
/// </summary>
public sealed class ApiResult : IResult, IStatusCodeHttpResult, IValueHttpResult
{
    private ApiResult(int statusCode, object? value, bool isProblem)
    {
        StatusCode = statusCode;
        Value = value;
        IsProblem = isProblem;
    }

    public int StatusCode { get; }
    int? IStatusCodeHttpResult.StatusCode => StatusCode;
    public object? Value { get; }
    public bool IsProblem { get; }
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public static ApiResult Ok(object? value) => new(StatusCodes.Status200OK, value, false);
    public static ApiResult Created(object? value) => new(StatusCodes.Status201Created, value, false);
    public static ApiResult Accepted(object? value) => new(StatusCodes.Status202Accepted, value, false);
    public static ApiResult NoContent() => new(StatusCodes.Status204NoContent, null, false);

    public static ApiResult Problem(int status, string code, string title, string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"https://nodus.app/problems/{code}",
            ["title"] = title,
            ["status"] = status,
            ["code"] = code
        };
        if (detail is not null) body["detail"] = detail;
        if (extensions is not null)
            foreach (var (key, value) in extensions) body[key] = value;
        return new ApiResult(status, body, true);
    }

    public static ApiResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        Problem(StatusCodes.Status400BadRequest, "validation_failed", "Request validation failed.",
            extensions: new Dictionary<string, object?> { ["errors"] = errors });

    public static ApiResult NotFound(string what = "Resource") =>
        Problem(StatusCodes.Status404NotFound, "not_found", $"{what} was not found.");

    /// <summary>Replays a stored response verbatim.</summary>
    public static ApiResult Stored(int statusCode, JsonElement? body) =>
        new(statusCode, body, statusCode >= 400);

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCode;
        if (Value is null || StatusCode == StatusCodes.Status204NoContent) return;
        httpContext.Response.ContentType = IsProblem ? "application/problem+json" : "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(httpContext.Response.Body, Value, Value.GetType(), NodusJson.Options,
            httpContext.RequestAborted);
    }
}

public static class ProblemCodes
{
    public const string Unauthorized = "unauthorized";
    public const string VersionConflict = "version_conflict";
    public const string IdempotencyConflict = "idempotency_key_reused";
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string RateLimited = "rate_limited";
}

/// <summary>Collects field errors in the same shape as ASP.NET validation problems.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public ValidationErrors Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(message);
        return this;
    }

    public ValidationErrors AddIf(bool condition, string field, string message) =>
        condition ? Add(field, message) : this;

    public ApiResult ToResult() => ApiResult.Validation(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
}
