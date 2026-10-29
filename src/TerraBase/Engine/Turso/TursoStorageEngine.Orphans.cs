using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IOrphanDatabaseFiles
{
    public async ValueTask<bool?> IsBusyAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var (key, _) = Locate(database);

        if (!await _creation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Ledger.FailedClosed is not null ? null : _pendingIntents.Contains(key) || _initializing.ContainsKey(key) || _publishing.ContainsKey(key)
                || _stores.TryGetValue(key, out var store) && Ledger.Refusal(store) is not null
;
        }
        finally
        {
            _creation.Release();
        }
    }
}
