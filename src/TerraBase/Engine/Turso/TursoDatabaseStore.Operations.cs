using Turso.Data.Sqlite;
using Turso.Raw.Public;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    // Serialize the durable 0 -> 1 and 1 -> 0 transitions, never native work or writer acquisition.
    private readonly SemaphoreSlim _operations = new(1, 1);
    private int _activeOperations;

    // Completed when the last operation carrying a write intent ends; guarded by _operations.
    private TaskCompletionSource? _operationsEnded;

    /// <summary>
    /// Starts a storage operation. With <paramref name="intent"/> (anything that can write or sync the file) the first active
    /// one records the write intent. A read-only transaction (<c>query_only</c>) passes false: it cannot leave unsynced pages
    /// behind, and damage it detects is marked <c>Faulting</c>, with FULL, before its error reaches the caller (decision 0013d).
    /// </summary>
    internal async ValueTask<CopyOperation?> BeginOperationAsync(CancellationToken cancellationToken, bool intent = true)
    {
        if (Ledger is not { } ledger || IsSystem)
        {
            return null;
        }

        if (!intent)
        {
            return new CopyOperation(this, io.HasAdmission, intent: false);
        }

        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeOperations == 0)
            {
                await ledger.RecordIntentAsync(this, cancellationToken).ConfigureAwait(false);
            }
            _activeOperations++;
            return new CopyOperation(this, io.HasAdmission, intent: true);
        }
        finally
        {
            _operations.Release();
        }
    }

    internal sealed class CopyOperation(TursoDatabaseStore store, bool admitted, bool intent) : IAsyncDisposable
    {
        private bool _complete;
        public void Complete() => _complete = true;
        public ValueTask DisposeAsync() => store.EndOperationAsync(_complete, admitted, intent);
    }

    private async ValueTask EndOperationAsync(bool complete, bool admitted, bool intent)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!complete && CopyFault is null)
            {
                NoteCopyFault("a storage operation did not finish cleanup");
                await RecordCopyFaultAsync(CopyFault!).ConfigureAwait(false);
            }
            if (intent && --_activeOperations == 0)
            {
                _operationsEnded?.TrySetResult();
                _operationsEnded = null;
            }

            if (intent && _activeOperations == 0 && CopyFault is null && Ledger!.FailedClosed is null)
            {
                try
                {
                    if (admitted)
                    {
                        _ = await io.WithAdmissionAsync(async () =>
                        {
                            await Ledger.RetireIntentAsync(this).ConfigureAwait(false);
                            return true;
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        await Ledger.RetireIntentAsync(this).ConfigureAwait(false);
                    }
                }
                catch (StorageBusyException)
                {
                    // The operation finished; only its intent stays, which is conservative: the next operation records it again
                    // and retires it, a clean stop drops it at the next start, and after an unclean one it faults the copy unless
                    // the restart proof passes. Failing the node closed instead refused every write until a restart.
                    logger?.ZLogWarning($"Retiring the write intent of database {Tenant!.DatabaseId:@Database} found storage busy; the intent stays until its next operation ends");
                }
            }
        }
        catch (Exception error)
        {
            Ledger!.FailClosed($"a storage operation's intent could not be retired: {error.GetType().Name}");
            throw;
        }
        finally
        {
            _operations.Release();
        }
    }

    internal async Task RecordOperationDamageAsync(Exception error)
    {
        if (TursoStorageTransaction.Damaged(error) || error is TursoNativeException { StatusCode: 132 or 133 or 134 })
        {
            NoteCopyFault("a storage operation reported I/O failure or corruption");
            await RecordCopyFaultAsync(CopyFault!).ConfigureAwait(false);
        }
    }

    // Shutdown still checkpoints healthy files. Its own operation protects this disk work; idle pool disposal does none.
    internal async Task CheckpointOnCloseAsync()
    {
        if (CurrentWriter() is null || CloseRefusal is not null)
        {
            return;
        }

        await using var operation = await BeginOperationAsync(CancellationToken.None).ConfigureAwait(false);
        await AcquireWriterAsync("the closing checkpoint", CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (CurrentWriter() is { } writer && CloseRefusal is null)
            {
                await WriteLane.RunAsync(() =>
                {
                    if (Revisions.IsAttachedTo(writer.Connection))
                    {
                        Revisions.Checkpoint(writer.Connection);
                    }
                    else
                    {
                        Scalar(writer.Connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
                    }
                }, CancellationToken.None).ConfigureAwait(false);
            }
            operation?.Complete();
        }
        catch (SqliteException error)
        {
            if (TursoStorageTransaction.Damaged(error))
            {
                NoteCopyFault("the closing checkpoint reported I/O failure or corruption");
                await RecordCopyFaultAsync(CopyFault!).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            ReleaseWriter();
        }
    }
}
