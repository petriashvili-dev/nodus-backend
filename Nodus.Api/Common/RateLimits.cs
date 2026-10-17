using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Nodus.Api.Common;

public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    public int AuthPerMinute { get; init; } = 20;
    public int OtpPerMinute { get; init; } = 10;
    public int AiPerMinute { get; init; } = 10;
}

public static class RateLimits
{
    /// <summary>Anonymous registration, refresh and logout, partitioned by client IP.</summary>
    public const string Auth = "auth";

    /// <summary>Email code requests and checks, partitioned by client IP (per-email limits live in the OTP service).</summary>
    public const string Otp = "otp";

    /// <summary>AI requests, partitioned by user.</summary>
    public const string Ai = "ai";

    public static IServiceCollection AddNodusRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) => new ValueTask(ApiResult.Problem(StatusCodes.Status429TooManyRequests,
                ProblemCodes.RateLimited, "Too many requests, retry later.").ExecuteAsync(context.HttpContext));
            o.AddPolicy(Auth, http => PerMinute(ClientIp(http), limits.AuthPerMinute));
            o.AddPolicy(Otp, http => PerMinute(ClientIp(http), limits.OtpPerMinute));
            o.AddPolicy(Ai, http => PerMinute(http.User.FindFirstValue("sub") ?? ClientIp(http), limits.AiPerMinute));
        });
    }

    private static RateLimitPartition<string> PerMinute(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        });

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
