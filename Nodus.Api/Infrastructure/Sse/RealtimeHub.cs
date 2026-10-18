using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Sync;

namespace Nodus.Api.Infrastructure.Sse;

/// <summary>Invalidation only: the client reacts by calling Delta Sync. The event id is the recipient's sync revision.</summary>
public sealed record SyncSignal(long Revision);

public interface IRealtimeHub
{
    /// <summary>Subscribes one connection; dispose the result to unsubscribe.</summary>
    IRealtimeSubscription Subscribe(Guid userId);

    /// <summary>As <see cref="Subscribe"/>, unless the user already has <paramref name="maxConnections"/> open streams.</summary>
    IRealtimeSubscription? TrySubscribe(Guid userId, int maxConnections);

    void Publish(Guid userId, SyncSignal signal);
}

public interface IRealtimeSubscription : IDisposable
{
    ChannelReader<SyncSignal> Signals { get; }
}

/// <summary>
/// In-process fanout, sufficient for the single API instance. Lost signals are harmless: clients always catch up
/// through Delta Sync after reconnecting. Replace with PostgreSQL LISTEN/NOTIFY or a broker to scale out.
/// </summary>
public sealed class InMemoryRealtimeHub : IRealtimeHub
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Subscription, byte>> _subscribers = new();

    public IRealtimeSubscription Subscribe(Guid userId) => TrySubscribe(userId, int.MaxValue)!;

    public IRealtimeSubscription? TrySubscribe(Guid userId, int maxConnections)
    {
        var connections = _subscribers.GetOrAdd(userId, _ => new ConcurrentDictionary<Subscription, byte>());
        // Count and add atomically per user, so parallel connects cannot exceed the limit.
        lock (connections)
        {
            if (connections.Count >= maxConnections) return null;
            var subscription = new Subscription(this, userId);
            connections[subscription] = 0;
            return subscription;
        }
    }

    public void Publish(Guid userId, SyncSignal signal)
    {
        if (!_subscribers.TryGetValue(userId, out var connections)) return;
        foreach (var connection in connections.Keys) connection.Channel.Writer.TryWrite(signal);
    }

    private void Remove(Subscription subscription)
    {
        // The per-user dictionary is kept: removing it could race with a concurrent Subscribe of the same user.
        if (_subscribers.TryGetValue(subscription.UserId, out var connections))
            lock (connections) connections.TryRemove(subscription, out _);

        subscription.Channel.Writer.TryComplete();
    }

    private sealed class Subscription(InMemoryRealtimeHub hub, Guid userId) : IRealtimeSubscription
    {
        public Guid UserId { get; } = userId;

        // Only the newest revision matters, so a slow connection simply drops older signals.
        public Channel<SyncSignal> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<SyncSignal>(
            new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public ChannelReader<SyncSignal> Signals => Channel.Reader;

        public void Dispose() => hub.Remove(this);
    }
}

public sealed class SyncChangedOutboxHandler(IRealtimeHub hub) : IOutboxHandler
{
    public string EventType => OutboxEventTypes.SyncChanged;

    public Task HandleAsync(OutboxEvent outboxEvent, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<SyncChangedPayload>(outboxEvent.Payload, NodusJson.Options)!;
        foreach (var recipient in payload.Recipients) hub.Publish(recipient.UserId, new SyncSignal(recipient.Revision));
        return Task.CompletedTask;
    }
}
