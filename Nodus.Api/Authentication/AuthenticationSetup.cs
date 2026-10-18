using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Infrastructure.Resend;
using Resend;

namespace Nodus.Api.Authentication;

public static class AuthenticationSetup
{
    public static IServiceCollection AddNodusAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<EmailOtpOptions>().BindConfiguration(EmailOtpOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ResendOptions>().BindConfiguration(ResendOptions.Section);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, TimeProvider>((bearer, jwt, time) =>
            {
                var skew = TimeSpan.FromSeconds(30);
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = jwt.Value.GetSigningKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    // Lifetime is checked against the application clock, the same one that issued the token.
                    LifetimeValidator = (notBefore, expires, _, _) =>
                    {
                        var now = time.GetUtcNow().UtcDateTime;
                        return (notBefore is null || notBefore <= now + skew) && expires is { } e && e > now - skew;
                    }
                };
                bearer.Events = new JwtBearerEvents
                {
                    // A deleted account's tokens must stop working immediately, not when they expire.
                    OnTokenValidated = async context =>
                    {
                        if (!Guid.TryParse(context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
                        {
                            context.Fail("No subject.");
                            return;
                        }

                        var services = context.HttpContext.RequestServices;
                        var exists = await services.GetRequiredService<IMemoryCache>().GetOrCreateAsync(
                            UserExistsCacheKey(userId), async entry =>
                            {
                                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                                return await services.GetRequiredService<NodusDbContext>().Users
                                    .AnyAsync(u => u.Id == userId, context.HttpContext.RequestAborted);
                            });
                        if (!exists) context.Fail("The account no longer exists.");
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        await ApiResult.Problem(StatusCodes.Status401Unauthorized, ProblemCodes.Unauthorized,
                            "A valid access token is required.").ExecuteAsync(context.HttpContext);
                    }
                };
            });
        services.AddAuthorization();

        services.AddMemoryCache();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<AccountDeletionService>();
        services.AddScoped<IEmailOtpService, EmailOtpService>();

        if (string.IsNullOrWhiteSpace(configuration[$"{ResendOptions.Section}:ApiKey"]))
        {
            services.TryAddSingleton<IEmailSender, LoggingEmailSender>();
        }
        else
        {
            var proxy = configuration.GetSection($"{ResendOptions.Section}:Proxy").Get<ResendProxyOptions>() ?? new();
            if (!proxy.IsValid())
                throw new InvalidOperationException(
                    "Resend:Proxy:Url must be an absolute http, https or socks5 URL without credentials (use Username/Password).");
            services.Configure<ResendClientOptions>(o => o.ApiToken = configuration[$"{ResendOptions.Section}:ApiKey"]!);
            services.AddHttpClient<IResend, ResendClient>().ConfigurePrimaryHttpMessageHandler(proxy.CreateHandler);
            services.TryAddScoped<IEmailSender, ResendEmailSender>();
        }

        return services;
    }

    public static string UserExistsCacheKey(Guid userId) => $"user-exists:{userId}";

    /// <summary>The only source of the caller identity is the validated access token.</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw new InvalidOperationException("Authenticated principal has no user id.");
}
