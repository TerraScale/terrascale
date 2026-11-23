namespace TerraBase.Continuity;

public sealed record ContinuityRawKey(ReadOnlyMemory<byte> Bytes);

public sealed record ContinuityDatabaseKeyMissing(Ulid DatabaseId, string Message, string Code);

public readonly union ContinuityRawKeyOutcome(ContinuityRawKey, ContinuityDatabaseKeyMissing);

public sealed record CopyTargetDiscarded;

public sealed record CopyTargetDiscardFailed(string Message, string Code);

public readonly union DiscardCopyTargetOutcome(CopyTargetDiscarded, CopyTargetDiscardFailed);

public sealed record ContinuitySessionMissing(Ulid DatabaseId, string Message, string Code);

public sealed record ContinuitySessionUnavailable(string Message, string Code);

public readonly union ContinuitySessionOutcome(ISqlDatabaseSession, ContinuitySessionMissing, ContinuitySessionUnavailable);

public interface IContinuityDatabaseCatalog
{
    ValueTask<IReadOnlyList<Ulid>> ListActiveDatabasesAsync(CancellationToken cancellationToken);

    ValueTask<ContinuityRawKeyOutcome> ResolveRawKeyAsync(Ulid databaseId, CancellationToken cancellationToken);

    ValueTask DatabaseReplacedAsync(Ulid databaseId, CausalBookmark? rewoundTo, CancellationToken cancellationToken);

    ValueTask<DiscardCopyTargetOutcome> DiscardCopyTargetAsync(Ulid databaseId, CancellationToken cancellationToken);
}

public sealed class ContinuitySessions(ISqlStorageEngine engine, IContinuityDatabaseCatalog catalog)
{
    public ISqlStorageEngine Engine => engine;

    public IContinuityDatabaseCatalog Catalog => catalog;

    public async ValueTask<ContinuityRawKeyOutcome> ResolveKeyAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var resolved = await catalog.ResolveRawKeyAsync(databaseId, cancellationToken);
        return resolved switch
        {
            ContinuityRawKey key when key.Bytes.Length == DatabaseOpenRequest.RawKeyLength => key,
            ContinuityRawKey => new ContinuityDatabaseKeyMissing(databaseId, "the catalog returned a raw key that is not 32 bytes", "invalid_database_key"),
            ContinuityDatabaseKeyMissing missing => missing,
        };
    }

    public async ValueTask<ContinuitySessionOutcome> OpenAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveKeyAsync(databaseId, cancellationToken);
        return resolved switch
        {
            ContinuityRawKey key => await OpenAsync(databaseId, key.Bytes, cancellationToken),
            ContinuityDatabaseKeyMissing missing => new ContinuitySessionMissing(missing.DatabaseId, missing.Message, missing.Code),
        };
    }

    public async ValueTask<ContinuitySessionOutcome> OpenAsync(Ulid databaseId, ReadOnlyMemory<byte> rawKey, CancellationToken cancellationToken)
    {
        var opened = await engine.OpenAsync(new DatabaseOpenRequest(databaseId, rawKey), cancellationToken);
        return opened switch
        {
            ISqlDatabaseSession session => session,
            DatabaseNotFound missing => new ContinuitySessionMissing(missing.DatabaseId, missing.Message, "not_found"),
            InvalidDatabaseRequest invalid => new ContinuitySessionUnavailable(invalid.Message, invalid.Code),
            StorageCorrupted corrupted => new ContinuitySessionUnavailable(corrupted.Message, "storage_corrupted"),
            StorageBusy busy => new ContinuitySessionUnavailable(busy.Message, "storage_busy"),
        };
    }
}
