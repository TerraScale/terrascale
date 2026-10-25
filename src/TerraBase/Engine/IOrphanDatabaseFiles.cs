using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>Conditional file cleanup serialized with lifecycle work and catalog registration.</summary>
internal interface IOrphanDatabaseFiles
{
    /// <summary>Closes and deletes only idle, unowned files authorized inside a catalog writer held through unlink.</summary>
    ValueTask<bool> DeleteAsync(TenantContext database, Func<IStorageTransaction, CancellationToken, ValueTask<bool>> authorize, CancellationToken cancellationToken);
}
