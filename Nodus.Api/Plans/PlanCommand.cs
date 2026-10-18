using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nodus.Api.Authentication;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Sync;

namespace Nodus.Api.Plans;

public enum PlanChangeOutcome
{
    Changed,
    Unchanged,
    UserNotFound
}

/// <summary>Sets plans by hand (test group). There is no HTTP endpoint for it: only operators with shell access run it.</summary>
public sealed class PlanService(NodusDbContext db, UnitOfWork unitOfWork, TimeProvider time, ILogger<PlanService> logger)
{
    /// <summary>The change is journaled for the user, so their devices receive the new <c>features</c> through sync.</summary>
    public async Task<PlanChangeOutcome> SetAsync(Guid? userId, string? email, UserPlan plan, DateTimeOffset? expiresAt,
        CancellationToken ct)
    {
        var user = userId is { } id
            ? await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct)
            : await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null) return PlanChangeOutcome.UserNotFound;

        PlanSource? source = plan == UserPlan.Standard ? null : PlanSource.Manual;
        if (plan == UserPlan.Standard) expiresAt = null;
        if (user.Plan == plan && user.PlanSource == source && user.PlanExpiresAt == expiresAt) return PlanChangeOutcome.Unchanged;

        user.Plan = plan;
        user.PlanSource = source;
        user.PlanExpiresAt = expiresAt;
        user.UpdatedAt = time.GetUtcNow();
        user.Version++;
        unitOfWork.Journal.Record(SyncEntityType.User, user.Id.ToString(), user.Version, SyncOperation.Upsert, [user.Id]);
        await unitOfWork.CommitAsync(ct);
        logger.LogInformation("Plan of user {UserId} set to {Plan} until {ExpiresAt}", user.Id, plan, expiresAt);
        return PlanChangeOutcome.Changed;
    }
}

/// <summary>
/// <c>plan set (--email E | --user-id ID) --plan standard|plus [--expires 2027-01-01T00:00:00Z]</c> and <c>plan list</c>.
/// Returns the process exit code.
/// </summary>
public static class PlanCommand
{
    private const string Usage = """
        Usage:
          plan set (--email <email> | --user-id <uuid>) --plan standard|plus [--expires <ISO 8601 instant>]
          plan list
        """;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        switch (args)
        {
            case ["list"]:
                await ListAsync(provider.GetRequiredService<NodusDbContext>(), provider.GetRequiredService<TimeProvider>(), output, ct);
                return 0;
            case ["set", .. var options]:
                return await SetAsync(provider.GetRequiredService<PlanService>(), provider.GetRequiredService<TimeProvider>(),
                    options, output, ct);
            default:
                await output.WriteLineAsync(Usage);
                return 2;
        }
    }

    private static async Task<int> SetAsync(PlanService plans, TimeProvider time, string[] args, TextWriter output,
        CancellationToken ct)
    {
        if (!TryParseSet(args, time.GetUtcNow(), out var userId, out var email, out var plan, out var expiresAt, out var error))
        {
            await output.WriteLineAsync($"{error}\n{Usage}");
            return 2;
        }

        var outcome = await plans.SetAsync(userId, email, plan, expiresAt, ct);
        await output.WriteLineAsync(outcome switch
        {
            PlanChangeOutcome.Changed => "Plan changed.",
            PlanChangeOutcome.Unchanged => "Plan is already set.",
            _ => "User not found."
        });
        return outcome == PlanChangeOutcome.UserNotFound ? 1 : 0;
    }

    private static bool TryParseSet(string[] args, DateTimeOffset now, out Guid? userId, out string? email, out UserPlan plan,
        out DateTimeOffset? expiresAt, out string error)
    {
        (userId, email, plan, expiresAt) = (null, null, UserPlan.Standard, null);
        var options = ParseOptions(args);
        if (options is null || options.Keys.Any(k => k is not ("--user-id" or "--email" or "--plan" or "--expires")))
            return Fail("Options are --user-id, --email, --plan and --expires, each followed by a value.", out error);

        if (options.TryGetValue("--user-id", out var rawId))
        {
            if (!Guid.TryParse(rawId, out var id)) return Fail("--user-id must be a UUID.", out error);
            userId = id;
        }

        if (options.TryGetValue("--email", out var rawEmail))
        {
            if (!EmailAddress.TryNormalize(rawEmail, out var normalized)) return Fail("--email is not a valid address.", out error);
            email = normalized;
        }

        if (userId is null == email is null) return Fail("Exactly one of --user-id and --email is required.", out error);

        switch (options.GetValueOrDefault("--plan"))
        {
            case "standard": plan = UserPlan.Standard; break;
            case "plus": plan = UserPlan.Plus; break;
            default: return Fail("--plan must be standard or plus.", out error);
        }

        if (options.TryGetValue("--expires", out var rawExpiry))
        {
            if (plan == UserPlan.Standard) return Fail("--expires applies only to the plus plan.", out error);
            if (!DateTimeOffset.TryParse(rawExpiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                return Fail("--expires must be an ISO 8601 instant, e.g. 2027-01-01T00:00:00Z.", out error);
            if (at <= now) return Fail("--expires must be in the future.", out error);
            expiresAt = at.ToUniversalTime();
        }

        error = "";
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    /// <summary>Users with a non-standard plan, expired ones included, so they can be renewed or reset.</summary>
    private static async Task ListAsync(NodusDbContext db, TimeProvider time, TextWriter output, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Where(u => u.Plan != UserPlan.Standard).OrderBy(u => u.CreatedAt)
            .Select(u => new { u.Id, u.Email, u.Plan, u.PlanExpiresAt, u.AiConsentAt })
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        foreach (var u in users)
        {
            var expiry = u.PlanExpiresAt is { } at ? (at <= now ? $"expired {at:O}" : $"until {at:O}") : "no expiry";
            await output.WriteLineAsync(
                $"{u.Id}\t{u.Email ?? "(anonymous)"}\t{u.Plan.ToString().ToLowerInvariant()}\t{expiry}\t" +
                (u.AiConsentAt is null ? "no AI consent" : "AI consent"));
        }

        await output.WriteLineAsync($"{users.Count} user(s).");
    }

    /// <summary><c>--name value</c> pairs; <c>null</c> on a dangling name or a repeated one.</summary>
    private static Dictionary<string, string>? ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0) return null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
                return null;
        return options;
    }
}
