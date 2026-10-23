using Turso.Data.Sqlite;
using Turso.Raw.Public;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteDatabaseStore
{
    // Serialize the durable 0 -> 1 and 1 -> 0 transitions, never native work or writer acquisition.
    private readonly SemaphoreSlim _operations = new(1, 1);
    private int _activeOperations;

    internal async ValueTask<CopyOperation?> BeginOperationAsync(CancellationToken cancellationToken)
    {
        if (Ledger is not { } ledger || IsSystem)
        {
            return null;
        }

        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeOperations == 0)
            {
                await ledger.RecordIntentAsync(this, cancellationToken).ConfigureAwait(false);
            }
            _activeOperations++;
            return new CopyOperation(this, io.HasAdmission);
        }
        finally
        {
            _operations.Release();
        }
    }

    internal sealed class CopyOperation(SqliteDatabaseStore store, bool admitted) : IAsyncDisposable
    {
        private bool _complete;
        public void Complete() => _complete = true;
        public ValueTask DisposeAsync() => store.EndOperationAsync(_complete, admitted);
    }

    private async ValueTask EndOperationAsync(bool complete, bool admitted)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!complete && CopyFault is null)
            {
                NoteCopyFault("a storage operation did not finish cleanup");
                await RecordCopyFaultAsync(CopyFault!).ConfigureAwait(false);
            }
            if (--_activeOperations == 0 && CopyFault is null && Ledger!.FailedClosed is null)
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
        if (SqliteStorageTransaction.Damaged(error) || error is TursoNativeException { StatusCode: 132 or 133 or 134 } or TursoNativeException { IsPageAuthenticationFailure: true })
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
                await WriteLane.RunAsync(() => Scalar(writer.Connection, null, "PRAGMA wal_checkpoint(TRUNCATE);"), CancellationToken.None).ConfigureAwait(false);
            }
            operation?.Complete();
        }
        catch (SqliteException error)
        {
            if (SqliteStorageTransaction.Damaged(error))
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
