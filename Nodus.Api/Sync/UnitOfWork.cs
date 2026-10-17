using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Api.Sync;

/// <summary>Saves entity changes, their sync feed entries and outbox events in one transaction.</summary>
public sealed class UnitOfWork(NodusDbContext db, ChangeJournal journal, OutboxSignal signal)
{
    private bool _signalPending;

    public ChangeJournal Journal => journal;

    /// <summary>
    /// Inside an ambient transaction (opened by the idempotency filter) the commit is left to its owner,
    /// which then calls <see cref="AfterCommit"/>.
    /// </summary>
    public async Task CommitAsync(CancellationToken ct)
    {
        var owned = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            _signalPending |= journal.HasPending;
            await journal.FlushAsync(ct);
            await db.SaveChangesAsync(ct);
            if (owned is null) return;
            await owned.CommitAsync(ct);
            AfterCommit();
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    public void AfterCommit()
    {
        if (_signalPending) signal.Notify();
        _signalPending = false;
    }

    /// <summary>Drops tracked and journaled changes of a failed or rejected operation.</summary>
    public void Discard()
    {
        journal.Clear();
        db.ChangeTracker.Clear();
        _signalPending = false;
    }
}
