namespace Nodus.Api.Sync;

/// <summary>What Delta Sync sends for one entity: its current state, a tombstone, or nothing.</summary>
public readonly record struct SyncEntityState(long? Version, object? Data, bool Skip = false)
{
    public static SyncEntityState Upsert(long version, object data) => new(version, data);

    /// <summary>Deleted or no longer accessible to the recipient.</summary>
    public static SyncEntityState Tombstone(long? version = null) => new(version, null);

    /// <summary>Nothing to send (another entity's tombstone covers it, or it is not meant for this recipient).</summary>
    public static readonly SyncEntityState Nothing = new(null, null, Skip: true);
}

/// <summary>
/// Supplies the current canonical state of one entity type for Delta Sync (strategy, selected by
/// <see cref="EntityType"/> through <c>ISelectionResolver</c>). Each provider lives in its module; a new syncable entity
/// adds a provider and never touches <c>SyncEndpoints</c>. Access is checked here, at read time.
/// </summary>
public interface ISyncEntityProvider
{
    SyncEntityType EntityType { get; }

    /// <summary>
    /// Loads all referenced entities in bulk. Ids missing from the result are sent as tombstones.
    /// </summary>
    Task<IReadOnlyDictionary<string, SyncEntityState>> LoadAsync(Guid userId, IReadOnlyCollection<string> entityIds,
        CancellationToken ct);
}

public static class SyncIds
{
    public static Guid[] Guids(IEnumerable<string> ids) =>
        ids.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToArray();
}
