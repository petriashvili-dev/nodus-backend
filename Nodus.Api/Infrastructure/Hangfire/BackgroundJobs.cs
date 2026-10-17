using System.Linq.Expressions;
using Hangfire;
using Hangfire.States;

namespace Nodus.Api.Infrastructure.Hangfire;

/// <summary>Enqueues background work. Called from outbox handlers so that no job is lost between commit and enqueue.</summary>
public interface IBackgroundJobs
{
    Task EnqueueAsync<TJob>(Expression<Func<TJob, Task>> job, string queue) where TJob : notnull;

    /// <summary>Delayed execution; the queue comes from the job method's <c>[Queue]</c> attribute.</summary>
    Task ScheduleAsync<TJob>(Expression<Func<TJob, Task>> job, TimeSpan delay) where TJob : notnull;
}

public sealed class HangfireBackgroundJobs(IBackgroundJobClient client) : IBackgroundJobs
{
    public Task EnqueueAsync<TJob>(Expression<Func<TJob, Task>> job, string queue) where TJob : notnull
    {
        client.Create(job, new EnqueuedState(queue));
        return Task.CompletedTask;
    }

    public Task ScheduleAsync<TJob>(Expression<Func<TJob, Task>> job, TimeSpan delay) where TJob : notnull
    {
        client.Create(job, new ScheduledState(delay));
        return Task.CompletedTask;
    }
}

/// <summary>Used when Hangfire is disabled (tests, tooling): runs the job immediately in its own scope.</summary>
public sealed class InlineBackgroundJobs(IServiceScopeFactory scopes) : IBackgroundJobs
{
    public async Task EnqueueAsync<TJob>(Expression<Func<TJob, Task>> job, string queue) where TJob : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        await job.Compile()(scope.ServiceProvider.GetRequiredService<TJob>());
    }

    /// <summary>Delays are ignored inline.</summary>
    public Task ScheduleAsync<TJob>(Expression<Func<TJob, Task>> job, TimeSpan delay) where TJob : notnull =>
        EnqueueAsync(job, "inline");
}
