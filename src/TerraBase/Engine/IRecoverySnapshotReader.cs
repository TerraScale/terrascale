using TerraBase.Tenancy;

namespace TerraBase.Engine;

public interface IRecoverySnapshot : IAsyncDisposable
{
    IStorageTransaction Transaction { get; }
}

public interface IRecoverySnapshotReader
{
    ValueTask<IRecoverySnapshot> OpenAsync(string file, TenantContext source, CancellationToken cancellationToken);
}
