using Turso.Data.Sqlite;
using Turso.Raw.Public;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private int _activeOperations;

    private TaskCompletionSource? _operationsEnded;

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
