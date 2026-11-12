using TerraBase.Tenancy;

namespace TerraBase.Engine;

public interface IStorageEngine : IAsyncDisposable
{
    string Name { get; }

    StoragePressure StoragePressure { get; }

    long CommittedWrites { get; }

    long? IoAdmittedCount { get; }

    ValueTask RefreshStoragePressureAsync(CancellationToken cancellationToken);

    bool SystemDatabaseExists { get; }

    ValueTask<bool> CreateDatabaseWithIntentAsync(
        TenantContext database, TenantContext? source, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken);

    ValueTask CompleteDatabaseIntentAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<bool> RecordDatabaseDeleteIntentAsync(
        TenantContext database, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken);

    ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken);

    event EventHandler<IDatabaseStore>? DatabaseDeleted;

    ValueTask<bool> CreateDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<IDatabaseStore?> OpenDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    IAsyncEnumerable<TenantContext> ListDatabasesAsync(CancellationToken cancellationToken);

    ValueTask<bool> CopyDatabaseAsync(TenantContext source, TenantContext target, CancellationToken cancellationToken);

    ValueTask<bool> DeleteDatabaseAsync(TenantContext database, CancellationToken cancellationToken, bool retainIntentOwnership = false, Ulid? incarnation = null);

    ValueTask<bool> VerifyDatabaseErasureAsync(TenantContext database, CancellationToken cancellationToken);

    IEnumerable<StoredDatabaseFile> EnumerateDatabaseFiles(CancellationToken cancellationToken = default);

    bool HasDatabaseFiles(TenantContext database);

    ValueTask<long?> GetDatabaseSizeAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<IDatabaseStore> OpenSystemDatabaseAsync(CancellationToken cancellationToken);
}
