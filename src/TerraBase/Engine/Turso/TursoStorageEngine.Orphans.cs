using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IOrphanDatabaseFiles
{
    public async ValueTask<bool?> IsBusyAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var (key, path) = Locate(database);
        // Observation must not wait for an in-progress lifecycle operation to finish and then age its file.
        if (!await _creation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool salvaging;
            lock (_salvageGate)
            {
                salvaging = _salvageOperations.ContainsKey(path);
            }
            return Ledger.FailedClosed is not null ? null : salvaging || _pendingIntents.Contains(key) || _initializing.ContainsKey(key) || _publishing.ContainsKey(key)
                || _stores.TryGetValue(key, out var store) && Ledger.Refusal(store) is not null
                || Directory.Exists(Path.GetDirectoryName(path))
                    && Directory.EnumerateFiles(Path.GetDirectoryName(path)!, $".{key.Database}.*.rebuild*").Any();
        }
        finally
        {
            _creation.Release();
        }
    }
}
