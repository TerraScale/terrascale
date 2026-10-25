using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IOrphanDatabaseFiles
{
    private readonly HashSet<(string Organization, string Project, string Database)> _restoreReservations = [];

    public async ValueTask<IAsyncDisposable> ProtectRestoreAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var key = Key(database);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_restoreReservations.Count == MaxPendingIntents || !_restoreReservations.Add(key))
            {
                throw new IOException("too many active restores or the database is already restoring");
            }
            return new RestoreReservation(this, key);
        }
        finally
        {
            ExitCreation();
        }
    }

    private sealed class RestoreReservation(TursoStorageEngine engine, (string Organization, string Project, string Database) key) : IAsyncDisposable
    {
        private int _released;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }
            await engine.EnterCreationAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _ = engine._restoreReservations.Remove(key);
            }
            finally
            {
                engine.ExitCreation();
            }
        }
    }

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
            return Ledger.FailedClosed is not null ? null : OrphanBusyLocked(key, path);
        }
        finally
        {
            _creation.Release();
        }
    }

    private bool OrphanBusyLocked((string Organization, string Project, string Database) key, string path)
    {
        bool salvaging;
        lock (_salvageGate)
        {
            salvaging = _salvageOperations.ContainsKey(path);
        }
        return Ledger.FailedClosed is not null || salvaging || _restoreReservations.Contains(key) || _pendingIntents.Contains(key) || _initializing.ContainsKey(key) || _publishing.ContainsKey(key)
            || _stores.TryGetValue(key, out var store) && Ledger.Refusal(store) is not null
            || Directory.Exists(Path.GetDirectoryName(path))
                && Directory.EnumerateFiles(Path.GetDirectoryName(path)!, $".{key.Database}.*.rebuild*").Any();
    }
}
