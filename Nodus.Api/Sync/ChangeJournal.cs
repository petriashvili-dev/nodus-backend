using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Api.Sync;

/// <summary>Collects the sync changes of one unit of work and writes them atomically with the data.</summary>
public sealed class ChangeJournal(NodusDbContext db, TimeProvider time)
{
    private readonly List<(SyncEntityType Type, string Id, long Version, SyncOperation Operation, Guid[] Recipients)> _pending = [];

    public bool HasPending => _pending.Count > 0;

    public void Record(SyncEntityType type, string entityId, long version, SyncOperation operation, IEnumerable<Guid> recipients) =>
        _pending.Add((type, entityId, version, operation, recipients.Distinct().ToArray()));

    public void Clear() => _pending.Clear();

    /// <summary>
    /// Allocates per-recipient revisions and adds <see cref="SyncChange"/> rows plus one <c>sync.changed</c>
    /// outbox event. Must run inside the transaction that saves the data; recipients are locked in id order
    /// to avoid deadlocks between concurrent shared-node writes.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        if (_pending.Count == 0) return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Sync changes must be flushed inside a transaction.");

        var now = time.GetUtcNow();
        var byRecipient = _pending
            .SelectMany(c => c.Recipients.Select(r => (Recipient: r, Change: c)))
            .GroupBy(x => x.Recipient)
            .OrderBy(g => g.Key);
        var signals = new List<SyncSignalRecipient>();

        foreach (var group in byRecipient)
        {
            var changes = group.Select(x => x.Change).ToList();
            var last = await db.Database
                .SqlQuery<long>($"UPDATE users SET sync_revision = sync_revision + {changes.Count} WHERE id = {group.Key} RETURNING sync_revision AS \"Value\"")
                .ToListAsync(ct);
            if (last.Count == 0) continue; // recipient account no longer exists

            var revision = last[0] - changes.Count;
            foreach (var change in changes)
                db.SyncChanges.Add(new SyncChange
                {
                    RecipientUserId = group.Key, Revision = ++revision, EntityType = change.Type, EntityId = change.Id,
                    Operation = change.Operation, EntityVersion = change.Version, CreatedAt = now
                });
            signals.Add(new SyncSignalRecipient(group.Key, revision));
        }

        if (signals.Count > 0)
            db.OutboxEvents.Add(OutboxEvent.Create(OutboxEventTypes.SyncChanged,
                JsonSerializer.Serialize(new SyncChangedPayload(signals), NodusJson.Options), now));
        _pending.Clear();
    }
}

public sealed record SyncSignalRecipient(Guid UserId, long Revision);

public sealed record SyncChangedPayload(IReadOnlyList<SyncSignalRecipient> Recipients);
