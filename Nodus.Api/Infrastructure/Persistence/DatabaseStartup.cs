using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Nodus.Api.Infrastructure.Persistence;

public sealed class DatabaseStartupOptions
{
    public const string Section = "Database";
    [Range(1, 1800)] public int StartupTimeoutSeconds { get; init; } = 180;
    [Range(1, 30)] public int StartupRetrySeconds { get; init; } = 2;
}

/// <summary>Swarm has no dependency ordering. These commands only wait; migration is a separate task.</summary>
public static class DatabaseStartup
{
    public static Task<int> RunAsync(IServiceProvider services, bool requireSchema, TextWriter output, CancellationToken ct)
    {
        var options = services.GetRequiredService<IOptions<DatabaseStartupOptions>>().Value;
        var time = services.GetRequiredService<TimeProvider>();
        return WaitAsync(async token =>
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            if (!await db.Database.CanConnectAsync(token)) return false;
            token.ThrowIfCancellationRequested();
            return !requireSchema || !(await db.Database.GetPendingMigrationsAsync(token)).Any();
        }, requireSchema ? "database and release migrations" : "database", options, time, output, ct);
    }

    internal static async Task<int> WaitAsync(Func<CancellationToken, Task<bool>> ready, string description,
        DatabaseStartupOptions options, TimeProvider time, TextWriter output, CancellationToken ct,
        Func<CancellationToken, Task>? retry = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.StartupTimeoutSeconds), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                try
                {
                    var isReady = await ready(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    if (isReady) return 0;
                }
                catch (NpgsqlException ex) when (ex.IsTransient) { }
                catch (TimeoutException) { }
                await output.WriteLineAsync($"Waiting for {description}.");
                if (retry is not null) await retry(linked.Token);
                else await Task.Delay(TimeSpan.FromSeconds(options.StartupRetrySeconds), time, linked.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            await output.WriteLineAsync($"Timed out waiting for {description}; refusing to start.");
            return 1;
        }
    }
}
