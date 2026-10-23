using Turso.Data.Sqlite;
using Turso.Raw.Public;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    // Serialize the durable 0 -> 1 and 1 -> 0 transitions, never native work or writer acquisition.
    private readonly SemaphoreSlim _operations = new(1, 1);
    private int _activeOperations;

    // Completed when the last operation carrying a write intent ends; guarded by _operations.
    private TaskCompletionSource? _operationsEnded;

    /// <summary>How long a copy's intent outlives its last operation, so back-to-back writes record and retire it once (decision 0013f).</summary>
    internal static readonly TimeSpan IntentLinger = TimeSpan.FromSeconds(1);

    // Under _operations: the intent is recorded and not yet retired; each end of the last operation moves the generation,
    // so only the latest scheduled retire runs.
    private bool _intentHeld;
    private long _idleGeneration;
    private bool _closing;

    /// <summary>The clock <see cref="IntentLinger"/> runs on; integration tests replace it.</summary>
    internal TimeProvider IntentClock { get; set; } = TimeProvider.System;

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
            // A lingering intent still covers this operation; a refused copy records its own (FULL) every time, as before.
            if (_activeOperations == 0 && (!_intentHeld || ledger.Refusal(this) is not null))
            {
                await ledger.RecordIntentAsync(this, cancellationToken).ConfigureAwait(false);
                _intentHeld = true;
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
                if (!_closing)
                {
                    // Retired once the copy has stayed idle, not at the end of every write (decision 0013f).
                    var generation = ++_idleGeneration;
                    _ = RetireWhenIdleAsync(generation);
                }
                else if (admitted)
                {
                    _ = await io.WithAdmissionAsync(async () =>
                    {
                        await RetireHeldIntentAsync().ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                }
                else
                {
                    await RetireHeldIntentAsync().ConfigureAwait(false);
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

    // Under _operations, with no active operation: retires a lingering intent now, and cancels its scheduled retire.
    private Task RetireLingeringIntentAsync()
    {
        _idleGeneration++;
        return RetireHeldIntentAsync();
    }

    // Under _operations, with no active operation.
    private async Task RetireHeldIntentAsync()
    {
        if (_intentHeld)
        {
            await Ledger!.RetireIntentAsync(this).ConfigureAwait(false);
            _intentHeld = false;
        }
    }

    private async Task RetireWhenIdleAsync(long generation)
    {
        await Task.Delay(IntentLinger, IntentClock).ConfigureAwait(false);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            // A later operation or a closed store keeps it: a closed copy's intent is retired by its close, or proven at the
            // next start, never deleted under a store of the same incarnation that opened since.
            if (generation == _idleGeneration && _activeOperations == 0 && !Closed && CopyFault is null && Ledger!.FailedClosed is null)
            {
                await RetireHeldIntentAsync().ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // The node is stopping (its journal or ledger closed under the timer): the intent stays, and the next start
            // proves the copy, as after any other ending with an intent.
        }
        catch (Exception error)
        {
            Ledger!.FailClosed($"a storage operation's intent could not be retired: {error.GetType().Name}");
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
        // From here the last operation retires a held intent at once: a clean shutdown leaves none to prove.
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            _closing = true;
            if (_activeOperations == 0 && CopyFault is null && Ledger?.FailedClosed is null)
            {
                await RetireHeldIntentAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _operations.Release();
        }

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
