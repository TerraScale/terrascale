namespace TerraBase.Continuity;

public interface IContinuityDatabaseCatalog
{
    ValueTask<IReadOnlyList<Ulid>> ListActiveDatabasesAsync(CancellationToken cancellationToken);

    ValueTask<Result<ReadOnlyMemory<byte>, NotFound>> ResolveRawKeyAsync(Ulid databaseId, CancellationToken cancellationToken);

    ValueTask DatabaseReplacedAsync(Ulid databaseId, CancellationToken cancellationToken);

    ValueTask<Result<Done, Unavailable>> DiscardCopyTargetAsync(Ulid databaseId, CancellationToken cancellationToken);
}

public sealed class ContinuitySessions(ISqlStorageEngine engine, IContinuityDatabaseCatalog catalog)
{
    public ISqlStorageEngine Engine => engine;

    public IContinuityDatabaseCatalog Catalog => catalog;

    public async ValueTask<Result<ReadOnlyMemory<byte>, NotFound>> ResolveKeyAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        await catalog.ResolveRawKeyAsync(databaseId, cancellationToken).ConfigureAwait(false) switch
        {
            ReadOnlyMemory<byte> key when key.Length == DatabaseOpenRequest.RawKeyLength => key,
            ReadOnlyMemory<byte> => new NotFound("the catalog returned a raw key that is not 32 bytes", "invalid_database_key"),
            NotFound notFound => notFound,
        };

    public async ValueTask<Result<ISqlDatabaseSession, NotFound, Unavailable>> OpenAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        await ResolveKeyAsync(databaseId, cancellationToken).ConfigureAwait(false) switch
        {
            ReadOnlyMemory<byte> key => await OpenAsync(databaseId, key, cancellationToken).ConfigureAwait(false),
            NotFound notFound => notFound,
        };

    public async ValueTask<Result<ISqlDatabaseSession, NotFound, Unavailable>> OpenAsync(Ulid databaseId, ReadOnlyMemory<byte> rawKey, CancellationToken cancellationToken) =>
        await engine.OpenAsync(new DatabaseOpenRequest(databaseId, rawKey), cancellationToken).ConfigureAwait(false) switch
        {
            ISqlDatabaseSession session => Opened(session),
            NotFound notFound => notFound,
            Validation validation => new Unavailable(validation.Message, validation.Code),
            StorageCorrupted corrupted => new Unavailable(corrupted.Message, "storage_corrupted"),
            StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
        };

    private static Result<ISqlDatabaseSession, NotFound, Unavailable> Opened(ISqlDatabaseSession session) => session;
}
