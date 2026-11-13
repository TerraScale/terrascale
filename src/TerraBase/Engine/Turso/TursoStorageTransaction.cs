using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Turso.Data.Sqlite;
using TerraBase.Features.Qos;

using TerraBase.Features.Sql;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction(
    TursoDatabaseStore store, TursoSession session, SqliteTransaction transaction, TursoRunner runner, bool write, Action? committed,
    bool ownsAdmission)
    : IStorageTransaction
{
    internal TursoDatabaseStore.CopyOperation? Operation { get; init; }

    internal string? Leased { get; init; }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _disposalGate = new();
    private Task? _disposal;
    private bool _streaming;

    private bool _faulted;

    private bool _committed;

    private long? _offset;

    private (long? Offset, bool KindsChanged)? _savepoint;

    internal bool LimitsReached { get; private set; }

    internal CommitResult? Committed { get; private set; }

    internal bool ChangedReaderState { get; private set; }

    public ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken) =>
        RunAsync(() => (long?)session.Command(TursoStatements.CurrentOffset, transaction).ExecuteScalar() ?? 0, cancellationToken);

    private T WithQueryBudget<T>(Func<T> execute, CancellationToken cancellationToken)
    {
        var budget = StartQueryBudget(cancellationToken);
        try
        {
            var result = execute();
            if (budget.Exceeded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new QueryCapacityException("query execution budget exceeded");
            }

            return result;
        }
        catch (SqliteException error) when (TursoSession.Stopped(error))
        {
            throw Exhausted(error, cancellationToken);
        }
        finally
        {
            StopQueryBudget();
        }
    }

    private QueryBudget? _queryBudget;

    private QueryBudget StartQueryBudget(CancellationToken cancellationToken)
    {
        var budget = new QueryBudget(store.Time, store.QueryBudget,
            !store.IsSystem && QosContext.Current is { } tenant ? QosPolicy.For(tenant.Plan) : null,
            store.SchemaMemory is { } memory ? new QosContext.ResultMemory(memory) : null);
        session.Connection.SetFtsProgressHandler((work, bytes) =>
            cancellationToken.IsCancellationRequested || budget.Native(work, bytes));

        var rows = 0;
        session.StopWhen(() => cancellationToken.IsCancellationRequested || (++rows % 32 == 0 && budget.Exceeded));
        session.Charge(size => budget.Limited && budget.Scan(size), () => budget.Limited && budget.Row());
        return _queryBudget = budget;
    }

    private void StopQueryBudget()
    {
        session.Connection.SetFtsProgressHandler(null);
        _queryBudget?.Dispose();
        _queryBudget = null;
        session.StopWhen(null);
        session.Charge(null, null);
    }

    private static QueryCapacityException Exhausted(SqliteException error, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new QueryCapacityException("query execution budget exceeded", error);
    }

    private sealed class QueryBudget(TimeProvider time, TimeSpan defaultDeadline, QosPolicy? policy, QosContext.ResultMemory? memory) : IDisposable
    {
        private bool _running = true;
        private long _started = time.GetTimestamp();
        private TimeSpan _paused;

        private const long RowWork = 8;
        private const long ReadWork = 4;

        private readonly long _workLimit = policy?.QueryWork ?? 5_000_000;
        private long _rows;
        private long _bytes;
        private long _nativeBytes;
        private long _nativeReserved;
        private bool ScanExceeded { get; set; }

        private int _jsonReads;

        public void Charge(string sql) => _jsonReads = JsonRead().Count(sql);

        public long Work { get; private set; }

        public bool Limited => _workLimit != long.MaxValue
            || policy is not null && (policy.ScanRows != long.MaxValue || policy.ScanBytes != long.MaxValue);

        public bool Exceeded => ScanExceeded
            || (policy?.QueryTimeout ?? defaultDeadline) is var deadline && deadline != Timeout.InfiniteTimeSpan && Spent >= deadline;

        public bool Row()
        {
            Work += RowWork;
            ScanExceeded |= Work > _workLimit;
            return ScanExceeded;
        }

        public bool Scan(long size)
        {
            Work += RowWork + ReadWork * _jsonReads;
            if (Work > _workLimit || policy is not null && (_rows >= policy.ScanRows || size > policy.ScanBytes - _bytes))
            {
                ScanExceeded = true;
                return true;
            }

            _rows++;
            _bytes += size;
            return false;
        }

        public bool Native(ulong work, ulong bytes)
        {
            var byteLimit = Math.Min(policy?.ScanBytes ?? 128 * 1024 * 1024, 128 * 1024 * 1024);
            if (Exceeded || work > (ulong)Math.Max(0, _workLimit - Work)
                || bytes > (ulong)Math.Max(0, byteLimit - _nativeBytes))
            {
                ScanExceeded = true;
                return true;
            }

            Work += (long)work;
            _nativeBytes += (long)bytes;
            if (memory is not null && _nativeBytes > _nativeReserved)
            {
                var reserve = Math.Min(64 * 1024, byteLimit - _nativeReserved);
                reserve = Math.Max(reserve, _nativeBytes - _nativeReserved);
                try
                {
                    memory.Reserve(reserve, QosContext.Current);
                    _nativeReserved += reserve;
                }
                catch (QosMemoryException)
                {
                    ScanExceeded = true;
                    return true;
                }
            }

            return false;
        }

        public void Dispose() => memory?.Dispose();

        public TimeSpan Spent => _running ? _paused + time.GetElapsedTime(_started) : _paused;

        public void Resume()
        {
            if (!_running)
            {
                _started = time.GetTimestamp();
                _running = true;
            }
        }

        public void Pause()
        {
            if (_running)
            {
                _paused += time.GetElapsedTime(_started);
                _running = false;
            }
        }
    }

    public async ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(() =>
        {
            if (_faulted || Aborted)
            {
                throw new InvalidOperationException("an operation of this transaction failed, so it can only be rolled back");
            }

            RefuseKnownFault();

            long offset;
            var committing = false;
            try
            {
                offset = _offset ?? GetOffset();
                cancellationToken.ThrowIfCancellationRequested();
                RefuseKnownFault();
                if (store.Tenant is { } committer)
                {
                    store.Checkpoint?.Invoke("writer.commit", committer);
                }

                if (Leased is { } leased)
                {
                    store.ThrowIfUnleased(leased);
                }

                committing = true;
                transaction.Commit();
                _committed = true;

                if (_rewriteUncaptured)
                {
                    store.UncapturedCommit(_uncapturedBy);
                }
            }
            catch (SqliteException error) when (committing && !_committed && TursoSession.Stopped(error))
            {
                _faulted = true;
                throw new SqlStatementException(new Interrupted(), "the commit was interrupted before it took effect", transactionEnded: true, error);
            }
            catch (SqliteException error) when (committing && !_committed && SqlErrors.IsConstraintFailure(error.Message))
            {
                _faulted = true;
                var foreignKey = SqlErrors.IsForeignKeyFailure(error.Message);
                throw new SqlStatementException(
                    foreignKey ? new ForeignKeyFailure() : new Constraint(), foreignKey ? "FOREIGN KEY constraint failed" : "constraint failed", transactionEnded: true, error);
            }
            catch
            {
                _faulted = true;
                if (committing)
                {
                    if (_rewriteUncaptured)
                    {
                        store.UncapturedCommit(_uncapturedBy);
                    }

                    NoteCopyFault("an error at or after COMMIT");
                }

                if (committing && (_offset is not null || _sqlChanged))
                {
                    store.MoveCommitCount();
                }

                throw;
            }

            var result = new CommitResult(offset);
            Committed = result;
            return result;
        }, cancellationToken).ConfigureAwait(false);

        if (_offset is not null || _sqlChanged)
        {
            Follow("the commit notification", committed);
        }

        if (_followFailure is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        RefuseKnownFault();
        return result;
    }

    private void RefuseKnownFault()
    {
        store.ThrowIfUnavailable();
    }

    private Exception? _followFailure;

    private void Follow(string step, Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception error)
        {
            _followFailure ??= error;
            store.LogAfterCommit(step, error);
        }
    }

    private bool _savepointPending;
    private bool _releasePending;

    internal void Savepoint()
    {
        ThrowIfReadOnly();
        _savepoint = (_offset, ChangedReaderState);
        _savepointPending = true;
        LimitsReached = false;
    }

    internal void ReleaseSavepoint()
    {
        _ = _savepoint ?? throw new InvalidOperationException("no savepoint is open");
        if (_savepointPending)
        {

            _savepointPending = false;
        }
        else
        {
            _releasePending = true;
        }

        _savepoint = null;
    }

    internal ValueTask RollbackToSavepointAsync()
    {
        var saved = _savepoint ?? throw new InvalidOperationException("no savepoint is open");
        if (_savepointPending)
        {

            _savepointPending = false;
            Restore(saved);
            return ValueTask.CompletedTask;
        }

        return RunVoidAsync(() =>
        {
            transaction.Rollback(GroupSavepoint);
            transaction.Release(GroupSavepoint);
            Restore(saved);
        });
    }

    private void Restore((long? Offset, bool KindsChanged) saved)
    {
        _kinds = null;
        (_offset, ChangedReaderState) = (saved.Offset, saved.KindsChanged);
        _savepoint = null;
        _faulted = false;
    }

    private void RunPendingSavepoint()
    {
        if (_releasePending)
        {
            transaction.Release(GroupSavepoint);
            _releasePending = false;
        }

        if (_savepointPending)
        {
            transaction.Save(GroupSavepoint);
            _savepointPending = false;
        }
    }

    private const string GroupSavepoint = "batched";

    internal bool Aborted { get; private set; }

    private T Guarded<T>(Func<T> call)
    {
        if (Aborted)
        {
            throw new InvalidOperationException("a failed statement rolled this transaction back, so it can only be disposed");
        }

        RefuseKnownFault();
        var native = session.Native;
        OpenInterruptWindow(native);
        using var refused = store.Refused.Register(InterruptCustomerStatement);
        using var failed = store.Ledger?.Refused.Register(InterruptCustomerStatement);
        try
        {
            RefuseKnownFault();
            var result = call();
            RefuseKnownFault();
            return result;
        }
        catch (Exception) when (store.CloseRefusal is not null)
        {
            RefuseKnownFault();
            throw;
        }
        catch (Exception error) when (NoteDamage(error) || Ended())
        {
            Aborted = _faulted = true;
            throw;
        }
        finally
        {
            CloseInterruptWindow(native);
        }
    }

    private bool Ended()
    {
        using var probe = session.Connection.CreateCommand();
        probe.Transaction = transaction;
        probe.CommandText = "SELECT 1;";
        try
        {
            probe.Prepare();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private async ValueTask RunVoidAsync(Action call) => await RunAsync(() =>
    {
        call();
        return true;
    }, CancellationToken.None).ConfigureAwait(false);

    private bool _captureSuspended;
    private bool _rewriteUncaptured;
    private string? _uncapturedBy;

    internal ValueTask SuspendCaptureAsync(CancellationToken cancellationToken, bool rewriteRefreshOnly = false, [CallerMemberName] string? caller = null)
    {
        if (!write)
        {
            throw new InvalidOperationException("only a write transaction captures changes");
        }

        if (!store.Captures)
        {
            return ValueTask.CompletedTask;
        }

        return new(RunAsync(() =>
        {
            using var command = session.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = TursoSession.CaptureOff;
            command.ExecuteNonQuery();
            _captureSuspended = true;
            _rewriteUncaptured |= !rewriteRefreshOnly;
            if (!rewriteRefreshOnly)
            {
                _uncapturedBy = caller;
            }
            return true;
        }, cancellationToken).AsTask());
    }

    public ValueTask DisposeAsync()
    {

        lock (_disposalGate)
        {
            if (_streaming)
            {
                throw new InvalidOperationException("dispose the active changes stream before using the transaction");
            }
            return new(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await runner.RunAsync(() =>
            {
                using var mutation = write && !store.IsSystem && store.Tenant is { } database
                    ? store.Ledger?.Mutations(database).Enter() : null;
                if (write && store.CloseRefusal is not null)
                {
                    store.Discard(session);
                    return;
                }
                try
                {
                    try
                    {
                        session.TrimTableCommands();
                        if (write && !_committed)
                        {
                            session.ForgetSchema();
                        }
                    }
                    finally
                    {

                        transaction.Dispose();
                    }

                    if (write && store.Tenant is { } tenant)
                    {

                        store.Checkpoint?.Invoke("writer.reset", tenant);
                    }

                    if (_captureSuspended)
                    {

                        using var capture = session.Connection.CreateCommand();
                        capture.CommandText = TursoSession.CaptureOn;
                        capture.ExecuteNonQuery();
                    }

                    RestoreForeignKeys();

                    if (!write && store.Tenant is { } readingTenant)
                    {

                        store.Checkpoint?.Invoke("reader.reset", readingTenant);
                    }
                }

                catch (Exception error) when (NoteDamage(error) || _committed)
                {

                    store.Discard(session);
                    return;
                }
                catch
                {
                    store.Discard(session);
                    throw;
                }

                store.Return(session);
            }, CancellationToken.None).ConfigureAwait(false);
            Operation?.Complete();
        }
        finally
        {
            try
            {
                if (_copyFault is { } reason)
                {

                    await store.RecordCopyFaultAsync(reason).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    await store.EndAsync(write, ownsAdmission, Operation).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
    }

    private long GetOffset() => (long?)session.Command(TursoStatements.CurrentOffset, transaction).ExecuteScalar() ?? 0;

    private long Offset()
    {
        if (_offset is null)
        {
            var current = GetOffset();
            VersionClock.EnsureCanAllocate(current);
            var next = session.Command(TursoStatements.SetOffset, transaction);
            next.Parameters.AddWithValue("$value", current == long.MaxValue ? throw new OverflowException("the commit offset is exhausted") : current + 1);
            _offset = next.ExecuteNonQuery() == 1 ? current + 1 : throw new OverflowException("the commit offset is exhausted");
        }

        return _offset.Value;
    }

    private void ThrowIfReadOnly()
    {
        if (!write)
        {
            throw new InvalidOperationException("a read transaction cannot mutate database state");
        }

        if (_faulted)
        {
            throw new InvalidOperationException("an operation of this transaction failed, so it can only be rolled back");
        }
    }

    private Task EnterAsync(CancellationToken cancellationToken, bool stream = false, bool mutation = false)
    {
        lock (_disposalGate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_streaming)
            {
                if (mutation)
                {

                    _faulted = true;
                }
                throw new InvalidOperationException("dispose the active changes stream before using the transaction");
            }
            _streaming = stream;

            return _gate.WaitAsync(cancellationToken);
        }
    }

    private async Task EnterStreamAsync(CancellationToken cancellationToken)
    {

        var waiting = EnterAsync(cancellationToken, stream: true);
        try
        {
            await waiting.ConfigureAwait(false);
        }
        catch
        {
            lock (_disposalGate)
            {
                _streaming = false;
            }
            throw;
        }
    }

    private ValueTask<T> RunAsync<T>(Func<T> call, CancellationToken cancellationToken, bool mutation = false, bool retry = false) =>

        RunCoreAsync(EnterAsync(mutation ? CancellationToken.None : cancellationToken, mutation: mutation), call, mutation, retry, cancellationToken);

    private async ValueTask<T> RunCoreAsync<T>(Task waiting, Func<T> call, bool mutation, bool retry, CancellationToken cancellationToken)
    {
        await waiting.ConfigureAwait(false);
        try
        {
            if (retry)
            {
                return await session.ExecuteAsync(runner, () =>
                {
                    using var modifying = write ? store.EnterMutation() : null;
                    RunPendingSavepoint();
                    return Guarded(call);
                }, cancellationToken).ConfigureAwait(false);
            }

            return await runner.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var modifying = write ? store.EnterMutation() : null;
                RunPendingSavepoint();

                var result = Guarded(call);
                if (mutation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException error) when (TursoSession.Stopped(error) && cancellationToken.IsCancellationRequested)
        {
            if (mutation)
            {
                _faulted = true;
            }

            throw new OperationCanceledException("the storage operation was cancelled", error, cancellationToken);
        }
        catch when (_copyFault is not null)
        {

            await store.Flagged.ConfigureAwait(false);
            if (mutation)
            {
                _faulted = true;
            }

            throw;
        }
        catch when (mutation)
        {

            _faulted = true;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteCommand Command(string sql, string? kind = null)
    {
        var command = session.Command(sql, transaction);
        if (kind is not null)
        {
            command.Parameters.AddWithValue("$kind", kind);
        }

        return command;
    }

    private void CountRead(string path) => store.StorageReads?.Add(1, new KeyValuePair<string, object?>("path", path));

    private SqliteCommand PlainCommand(string sql)
    {
        var command = session.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    [GeneratedRegex(@"\b(?:json_(?:type|extract))\(", RegexOptions.CultureInvariant)]
    private static partial Regex JsonRead();
}
