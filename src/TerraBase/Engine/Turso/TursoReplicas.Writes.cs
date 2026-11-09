using TerraBase.Tenancy;
using Turso.Raw.Public;
using Turso.Raw.Public.Value;
using Turso.Raw.Public.Handles;
using TerraScale.TerraBase.Contracts;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoReplicas
{
    internal const string CaptureTable = "_ts_local_cdc";

    public ReplicaOverlay? Overlay { get; set; }

    public async ValueTask<IReplicaWriter?> BeginWriteAsync(TenantContext database, CancellationToken cancellationToken) =>
        await FindAsync(database).ConfigureAwait(false) is { Pulled: true } replica
            ? await replica.BeginWriteAsync(cancellationToken).ConfigureAwait(false)
            : null;

    private sealed partial class Replica
    {
        private const string CaptureOff = "PRAGMA unstable_capture_data_changes_conn('off');";
        private const string CaptureOn = "PRAGMA unstable_capture_data_changes_conn('full," + CaptureTable + "');";

        private TursoDatabaseHandle? _writer;

        internal bool Local { get; private set; }

        public async Task<IReplicaWriter?> BeginWriteAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _pulls.WaitAsync(cancellationToken).ConfigureAwait(false);
            IDisposable? exclusive = null;
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                // Native BEGIN/capture setup can refuse while a pooled reader begins or ends a transaction.
                // Use the same reader drain as page apply/checkpoint, and keep it through the short local commit.
                // A transient native refusal otherwise forwards home and makes the next write pay a WAN catch-up.
                exclusive = await _gate.ExclusiveAsync(cancellationToken).ConfigureAwait(false);
                return await OpenWriterAsync(captured: true, release: true, cancellationToken, exclusive).ConfigureAwait(false);
            }
            catch (TursoException error)
            {
                exclusive?.Dispose();
                _pulls.Release();
                // Only fixed native setup statements ran here; this detail contains no caller SQL or bound values.
                logger.ZLogInformation($"A local write goes home: replica writer setup failed ({error.GetType().Name:@Error}, {error.Message[..Math.Min(error.Message.Length, 512)]:@Detail})");
                return null;
            }
            catch
            {
                exclusive?.Dispose();
                _pulls.Release();
                throw;
            }
        }

        private async Task RestoreOverlayAsync(CancellationToken cancellationToken)
        {
            if (owner.Overlay is not { } overlay)
            {
                return;
            }

            await using var writer = await OpenWriterAsync(captured: false, release: false, cancellationToken).ConfigureAwait(false);
            Local = await overlay(database, Held, writer, cancellationToken).ConfigureAwait(false);
            if (Local)
            {
                await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<Writer> OpenWriterAsync(bool captured, bool release, CancellationToken cancellationToken, IDisposable? exclusive = null)
        {
            var native = _writer ??= await sync.ConnectHandleAsync(cancellationToken).ConfigureAwait(false);
            await Task.Run(() =>
            {
                Run(native, CaptureOff);
                if (captured)
                {
                    // No AUTOINCREMENT: the table is emptied by every capture, and a sqlite_sequence row would be a replica-only
                    // object that replica verification sees as divergence from the home.
                    if (TursoStorageTransaction.Counter(native, $"SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = '{CaptureTable}';") == 0)
                    {
                        Run(native, "CREATE TABLE IF NOT EXISTS turso_cdc_version (table_name TEXT PRIMARY KEY, version TEXT NOT NULL);");
                        Run(native, $"CREATE TABLE IF NOT EXISTS {CaptureTable} (change_id INTEGER PRIMARY KEY, change_time INTEGER, change_txn_id INTEGER, change_type INTEGER, table_name TEXT, id, before BLOB, after BLOB, updates BLOB);");
                        Run(native, $"INSERT OR IGNORE INTO turso_cdc_version (table_name, version) VALUES ('{CaptureTable}', 'v2');");
                    }

                    Run(native, "PRAGMA foreign_keys = ON;");
                    Run(native, CaptureOn);
                }
                else
                {
                    Run(native, "PRAGMA foreign_keys = OFF;");
                }

                Run(native, "BEGIN;");
            }, cancellationToken).ConfigureAwait(false);
            return new Writer(this, native, captured, release, exclusive);
        }

        private void EndWriter(TursoDatabaseHandle native, bool committed, bool release, IDisposable? exclusive)
        {
            try
            {
                if (!committed)
                {
                    Run(native, "ROLLBACK;");
                }

                Run(native, CaptureOff);
                Run(native, "PRAGMA foreign_keys = ON;");
            }
            catch (TursoException)
            {
                _writer = null;
                Close(native);
            }
            finally
            {
                exclusive?.Dispose();
                if (release)
                {
                    _pulls.Release();
                }
            }
        }

        private sealed class Writer(Replica replica, TursoDatabaseHandle native, bool captured, bool release, IDisposable? exclusive) : IReplicaWriter
        {
            private int _ended;
            private bool _committed;

            public ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(statement);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended) != 0, this);
                return new(Task.Run(() => Execute(statement, cancellationToken), cancellationToken));
            }

            public ValueTask<IReadOnlyList<CapturedRowChange>> CaptureAsync(CancellationToken cancellationToken)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended) != 0, this);
                return captured
                    ? new(Task.Run(Capture, cancellationToken))
                    : throw new InvalidOperationException("an overlay writer captures nothing");
            }

            public async ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended) != 0, this);
                await Task.Run(() => Run(native, "COMMIT;"), cancellationToken).ConfigureAwait(false);
                _committed = true;
                if (captured)
                {
                    replica.Local = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _ended, 1) == 0)
                {
                    replica.EndWriter(native, _committed, release, exclusive);
                }

                return ValueTask.CompletedTask;
            }

            private SqlStatementResult Execute(SqlStatement statement, CancellationToken cancellationToken)
            {
                try
                {
                    var (prepared, consumed) = TursoStorageTransaction.Prepare(native, statement.Sql, statement.First);
                    TursoBindings.SetQueryTimeout(native, statement.Timeout);
                    TursoBindings.SetMaxValueLength(native, statement.MaxBytes);
                    try
                    {
                        using (cancellationToken.Register(static state => TursoBindings.Interrupt((TursoDatabaseHandle)state!), native))
                        {
                            var (columns, rows, affected, truncated) = Reader.Step(prepared, statement, cancellationToken);
                            return new SqlStatementResult(columns, rows, affected, 0, 0, truncated, consumed);
                        }
                    }
                    finally
                    {
                        TursoBindings.SetQueryTimeout(native, TimeSpan.Zero);
                        TursoBindings.SetMaxValueLength(native, long.MaxValue);
                        TursoBindings.ClearInterrupt(native);
                    }
                }
                catch (TursoException error)
                {
                    var (failure, message) = TursoStorageTransaction.Classify(native, error);
                    throw new SqlStatementException(failure, message, transactionEnded: false, error);
                }
            }

            private IReadOnlyList<CapturedRowChange> Capture()
            {
                var rows = new List<(int Type, string Table, long RowId, byte[]? Before, byte[]? After, byte[]? Updates)>();
                var read = TursoBindings.PrepareStatement(native,
                    $"SELECT change_type, table_name, before, after, updates, id FROM {CaptureTable} WHERE change_type IN (-1, 0, 1) ORDER BY change_id;");
                try
                {
                    while (TursoBindings.Read(read))
                    {
                        rows.Add(((int)TursoBindings.GetValue(read, 0).IntValue, TursoBindings.GetValue(read, 1).StringValue, TursoBindings.GetValue(read, 5).IntValue,
                            Blob(read, 2), Blob(read, 3), Blob(read, 4)));
                    }
                }
                finally
                {
                    TursoStorageTransaction.Release(read);
                }

                Run(native, $"DELETE FROM {CaptureTable};");
                var columns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                var changes = new List<CapturedRowChange>(rows.Count);
                foreach (var (type, table, rowId, before, after, updates) in rows)
                {
                    if (TerraBaseSqlNames.IsInternalTable(table))
                    {
                        continue;
                    }

                    if (!columns.TryGetValue(table, out var names))
                    {
                        columns[table] = names = TableColumns(table);
                    }

                    bool[]? updated = updates is null ? null : [.. SqliteRecord.Decode(updates, names.Length).Select(flag => flag is long set && set != 0)];
                    changes.Add(new CapturedRowChange(table, type, names,
                        before is null ? null : SqliteRecord.Decode(before, names.Length), after is null ? null : SqliteRecord.Decode(after, names.Length), updated,
                        new CapturedImages(rowId, before, after)));
                }

                return changes;
            }

            private string[] TableColumns(string table)
            {
                var info = TursoBindings.PrepareStatement(native, $"PRAGMA table_xinfo(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");");
                try
                {
                    var names = new List<string>();
                    while (TursoBindings.Read(info))
                    {
                        if (TursoBindings.GetValue(info, 6).IntValue is not (1 or 2))
                        {
                            names.Add(TursoBindings.GetValue(info, 1).StringValue);
                        }
                    }

                    return [.. names];
                }
                finally
                {
                    TursoStorageTransaction.Release(info);
                }
            }

            private static byte[]? Blob(TursoStatementHandle statement, int column) =>
                TursoBindings.GetValueType(statement, column) == TursoValueType.Blob ? TursoBindings.GetValue(statement, column).BlobValue : null;
        }
    }
}
