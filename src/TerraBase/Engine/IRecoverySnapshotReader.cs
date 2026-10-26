using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>An isolated read transaction over a restored service scratch file, never registered as a database.</summary>
public interface IRecoverySnapshot : IAsyncDisposable
{
    /// <summary>The read-only historical transaction.</summary>
    IStorageTransaction Transaction { get; }
}

/// <summary>Opens existing recovery scratch without publishing it to the tenant engine or backup discovery.</summary>
public interface IRecoverySnapshotReader
{
    /// <summary>The caller retains file ownership and deletes it after the reader closes.</summary>
    ValueTask<IRecoverySnapshot> OpenAsync(string file, TenantContext source, CancellationToken cancellationToken);
}
