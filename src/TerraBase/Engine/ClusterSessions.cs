using TerraBase.Continuity;
using TerraBase.Features.Catalog;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Engine;

internal sealed class ClusterSessions(IServiceProvider services, ISqlStorageEngine engine, DatabaseKeyring keyring) : IContinuityDatabaseCatalog
{
    private const int CatalogPage = 512;

    private TenantDatabases Databases => services.GetRequiredService<TenantDatabases>();

    private SystemDatabase System => services.GetRequiredService<SystemDatabase>();

    private CatalogStore Catalog => services.GetRequiredService<CatalogStore>();

    public async ValueTask<Result<ISqlDatabaseSession, NotFound, Unavailable>> ResolveAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        await PlacementAsync(databaseId, cancellationToken) switch
        {
            DatabasePlacement placement => await Databases.OpenOrCreateAsync(databaseId, placement.KeyId, placement.Strict, cancellationToken) switch
            {
                ISqlDatabaseSession => await LeaseAsync(databaseId, placement.KeyId, cancellationToken),
                Unavailable unavailable => unavailable,
            },
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<IReadOnlyList<Ulid>> ListActiveDatabasesAsync(CancellationToken cancellationToken)
    {
        List<Ulid> active = [SystemDatabase.Id];
        string? after = null;
        while (await Catalog.AllDatabasesAsync(after, CatalogPage, cancellationToken) switch
        {
            IReadOnlyList<DatabaseRecord> records => records,
            Unavailable => null,
        } is { Count: > 0 } page)
        {
            active.AddRange(page.Where(static record => record.State != ControlDatabaseState.Deleted).Select(static record => record.Id));
            after = page[^1].Id.ToString();
        }

        return active;
    }

    public async ValueTask<Result<ReadOnlyMemory<byte>, NotFound>> ResolveRawKeyAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        await PlacementAsync(databaseId, cancellationToken) switch
        {
            DatabasePlacement placement => keyring.Derive(databaseId, placement.KeyId) switch
            {
                DatabaseKey key => new ReadOnlyMemory<byte>(key.RawKey),
                NotFound missing => missing,
            },
            NotFound missing => missing,
            Unavailable unavailable => new NotFound(unavailable.Message, unavailable.Code),
        };

    public async ValueTask DatabaseReplacedAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        await Databases.EvictAsync(databaseId);

    private async ValueTask<Result<DatabasePlacement, NotFound, Unavailable>> PlacementAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (databaseId == SystemDatabase.Id)
        {
            return new DatabasePlacement(System.KeyId, Strict: false);
        }

        return await Catalog.GetDatabaseAsync(databaseId, cancellationToken) switch
        {
            DatabaseRecord { State: ControlDatabaseState.Deleted } => new NotFound("the database was deleted", "database_deleted"),
            DatabaseRecord record => new DatabasePlacement(record.KeyId, record.Strict),
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<ISqlDatabaseSession, NotFound, Unavailable>> LeaseAsync(Ulid databaseId, string keyId, CancellationToken cancellationToken) =>
        keyring.Derive(databaseId, keyId) switch
        {
            DatabaseKey key => await engine.OpenAsync(new DatabaseOpenRequest(databaseId, key.RawKey), cancellationToken) switch
            {
                ISqlDatabaseSession session => Leased(session),
                NotFound missing => missing,
                Validation invalid => new Unavailable(invalid.Message, "database_key_mismatch"),
                StorageCorrupted corrupted => new Unavailable(corrupted.Message, "storage_corrupted"),
                StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
            },
            NotFound => new Unavailable("the database's master key is not configured on this node", "master_key_missing"),
        };

    private static Result<ISqlDatabaseSession, NotFound, Unavailable> Leased(ISqlDatabaseSession session) => new(session);

    private sealed record DatabasePlacement(string KeyId, bool Strict);
}
