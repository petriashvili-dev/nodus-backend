using Microsoft.EntityFrameworkCore;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Tests.Integration;

public sealed class DatabaseStartupTests
{
    [Fact]
    public async Task Waiter_retries_until_ready_without_starting_early()
    {
        var attempts = 0;
        var delays = 0;
        var code = await DatabaseStartup.WaitAsync(_ => Task.FromResult(++attempts == 3), "schema",
            new DatabaseStartupOptions(), TimeProvider.System, TextWriter.Null, CancellationToken.None,
            _ => { delays++; return Task.CompletedTask; });
        Assert.Equal(0, code);
        Assert.Equal(3, attempts);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task Transient_timeout_is_retried_but_programming_errors_are_not()
    {
        var attempts = 0;
        Assert.Equal(0, await DatabaseStartup.WaitAsync(_ => ++attempts == 1
                ? Task.FromException<bool>(new TimeoutException()) : Task.FromResult(true), "database",
            new DatabaseStartupOptions(), TimeProvider.System, TextWriter.Null, CancellationToken.None,
            _ => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseStartup.WaitAsync(
            _ => Task.FromException<bool>(new InvalidOperationException()), "database", new DatabaseStartupOptions(),
            TimeProvider.System, TextWriter.Null, CancellationToken.None));
    }

    [Fact]
    public async Task Readiness_does_not_override_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseStartup.WaitAsync(_ =>
        {
            cancel.Cancel();
            return Task.FromResult(true);
        }, "database", new DatabaseStartupOptions(), TimeProvider.System, TextWriter.Null, cancel.Token));
    }

    [Fact]
    public async Task Overall_deadline_also_cancels_an_unfinished_probe()
    {
        var output = new StringWriter();
        var code = await DatabaseStartup.WaitAsync(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }, "schema", new DatabaseStartupOptions { StartupTimeoutSeconds = 1 }, TimeProvider.System,
            output, CancellationToken.None);
        Assert.Equal(1, code);
        Assert.Contains("refusing to start", output.ToString());
    }

    [Fact]
    public async Task Database_connectivity_is_not_enough_when_a_release_migration_is_pending()
    {
        await using var app = new TestApp();
        app.Settings["Database:StartupTimeoutSeconds"] = "1";
        await app.InitializeAsync();
        Assert.Equal(0, await DatabaseStartup.RunAsync(app.Services, true, TextWriter.Null, CancellationToken.None));
        await app.WithDbAsync(async db =>
        {
            var last = (await db.Database.GetAppliedMigrationsAsync()).Last();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"__EFMigrationsHistory\" WHERE migration_id = {last}");
            return 0;
        });
        Assert.Equal(0, await DatabaseStartup.RunAsync(app.Services, false, TextWriter.Null, CancellationToken.None));
        Assert.Equal(1, await DatabaseStartup.RunAsync(app.Services, true, TextWriter.Null, CancellationToken.None));
        // Waiting never applies or marks a migration on its own.
        Assert.Equal(1, await app.WithDbAsync(async db => (await db.Database.GetPendingMigrationsAsync()).Count()));
    }
}
