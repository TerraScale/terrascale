using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Data.Common;
using Turso.Data.Sqlite;
using Turso.Raw.Public.Handles;

using TerraBase.Features.Sql;

namespace TerraBase.Engine.Turso;

internal sealed class TursoSession(SqliteConnection connection, TimeProvider time) : IDisposable
{
    private readonly Dictionary<string, SqliteCommand> _commands = [with(StringComparer.Ordinal)];

    private readonly Dictionary<string, SqliteCommand> _tableCommands = [with(StringComparer.Ordinal)];

    private const int MaxTableCommands = 256;

    private long _schema = -1;

    private SqliteTransaction? _checked;

    private bool? _queryOnly;
    private bool? _full;

    private Func<long, bool>? _scanCharge;
    private Func<bool>? _rowCharge;
    private Func<bool>? _stop;

    public SqliteConnection Connection => connection;

    internal TursoDatabaseHandle Native =>
        NativeHandle?.GetValue(connection) as TursoDatabaseHandle
        ?? throw new InvalidOperationException("the Turso driver no longer exposes its native connection handle");

    public int Statistics { get; set; }

    internal const string CaptureOn = "PRAGMA capture_data_changes_conn('full');";

    internal const string CaptureOff = "PRAGMA capture_data_changes_conn('off');";

    internal const string StoppedMessage = "terrabase: statement stopped";

    private const string ExpectChanges = "terrascale_expect_changes";

    public void Charge(Func<long, bool>? scan, Func<bool>? row)
    {
        _scanCharge = scan;
        _rowCharge = row;
    }

    private void RegisterFunctions()
    {
        connection.CreateFunction<long, long>("tb_qos_scan", size =>
        {
            if (_scanCharge?.Invoke(size) == true)
            {
                throw new OperationCanceledException(StoppedMessage);
            }

            ThrowIfStopped();
            return 1;
        });
        connection.CreateFunction<object?, long>("tb_sql_row", _ =>
        {
            if (_rowCharge?.Invoke() == true)
            {
                throw new OperationCanceledException(StoppedMessage);
            }

            ThrowIfStopped();
            return 1;
        }, isDeterministic: false);
        connection.CreateFunction<long, long, long>(ExpectChanges,
            (changed, expected) => changed == expected ? changed : throw new InvalidOperationException(SqlErrors.StaleRowMessage), isDeterministic: false);
    }

    public void StopWhen(Func<bool>? stop) => _stop = stop;

    public void ThrowIfStopped()
    {
        if (_stop?.Invoke() == true)
        {
            throw new OperationCanceledException(StoppedMessage);
        }
    }

    public static bool Stopped(SqliteException error) => error.SqliteErrorCode == 9
        || error.Message.Contains(StoppedMessage, StringComparison.Ordinal);

    public static bool Busy(SqliteException error) => SqlErrors.IsLocked(error.Message);

    public static byte[] Bytes(SqliteDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        byte[] bytes => bytes,
        string text => Encoding.UTF8.GetBytes(text),
        var other => throw new InvalidCastException($"column {ordinal} holds {other.GetType().Name}, not TEXT or BLOB"),
    };

    public static string Pragmas(long memoryBudget, int threads)
    {
        var pageCacheBytes = memoryBudget / 4;
        var cacheKib = Math.Max(2048, pageCacheBytes / threads / 1024);
        return string.Create(CultureInfo.InvariantCulture, $"""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -{cacheKib};
            PRAGMA foreign_keys = ON;
            """);
    }

    public static async Task<TursoSession> OpenAsync(TursoRunner io, string connectionString, string pragmas, TimeProvider time, CancellationToken cancellationToken, TursoDatabaseStore? store = null)
    {
        var connection = new SqliteConnection(connectionString)
        {
            CloseRefusal = store is null ? null : () => store.CloseRefusal,
        };
        var session = new TursoSession(connection, time);
        var opening = false;
        try
        {
            await io.RunAsync(() =>
            {
                store?.ThrowIfUnavailable();
                opening = true;
                OpenExisting(connection, new SqliteConnectionStringBuilder(connectionString).DataSource);
                if (store is not null)
                {
                    connection.UseManualWal();
                }
            }, cancellationToken).ConfigureAwait(false);
            await session.ExecuteAsync(io, () =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = pragmas;
                command.ExecuteNonQuery();
                session.RegisterFunctions();
                return true;
            }, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch (Exception error)
        {
            if (TursoStorageTransaction.Damaged(error))
            {
                store?.NoteCopyFault("the file or its log reported damage while opening a session");
            }
            if (Volatile.Read(ref opening))
            {
                await io.RunAsync(session.Dispose, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                session.Dispose();
            }

            throw;
        }
    }

    public static void OpenExisting(DbConnection connection, string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidDataException("the existing database main file is missing or empty");
        }

        connection.Open();
    }

    public async Task<T> ExecuteAsync<T>(TursoRunner io, Func<T> execute, CancellationToken cancellationToken)
    {
        var timeout = connection.DefaultTimeout;
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return await io.RunAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return execute();
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException error) when (Busy(error) && (timeout == 0 || Stopwatch.GetElapsedTime(started).TotalSeconds < timeout))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public SqliteCommand Command(string sql, SqliteTransaction transaction) => Kept(_commands, sql, transaction);

    public void Settings(bool queryOnly, bool full)
    {
        if (_queryOnly == queryOnly && _full == full)
        {
            return;
        }

        Drop(_commands);
        Drop(_tableCommands);
        _queryOnly = null;
        _full = null;
        using var command = connection.CreateCommand();
        command.CommandText = (queryOnly ? "PRAGMA query_only = 1;" : "PRAGMA query_only = 0;")
            + (full ? "PRAGMA synchronous = FULL;" : "PRAGMA synchronous = NORMAL;");
        command.ExecuteNonQuery();
        _queryOnly = queryOnly;
        _full = full;
    }

    public SqliteCommand TableCommand(string sql, SqliteTransaction transaction) => Kept(_tableCommands, sql, transaction);

    public void TrimTableCommands()
    {
        _checked = null;
        if (_tableCommands.Count > MaxTableCommands)
        {
            Drop(_tableCommands);
            return;
        }

        foreach (var command in _tableCommands.Values)
        {
            command.Parameters.Clear();
        }
    }

    private void CheckSchema(SqliteTransaction transaction)
    {
        _checked = transaction;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA schema_version;";
        var schema = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (schema != _schema)
        {
            Drop(_commands);
            Drop(_tableCommands);
            _schema = schema;
        }
    }

    public void ForgetSchema() => _schema = -1;

    private static void Drop(Dictionary<string, SqliteCommand> commands)
    {
        foreach (var command in commands.Values)
        {
            command.Dispose();
        }

        commands.Clear();
    }

    private SqliteCommand Kept(Dictionary<string, SqliteCommand> commands, string sql, SqliteTransaction transaction)
    {
        if (!ReferenceEquals(_checked, transaction))
        {
            CheckSchema(transaction);
        }

        if (!commands.TryGetValue(sql, out var command))
        {
            command = connection.CreateCommand();
            command.CommandText = sql;
            command.ReuseStatement = true;
            commands.Add(sql, command);
        }

        command.Transaction = transaction;
        command.Parameters.Clear();
        return command;
    }

    private static readonly FieldInfo? NativeHandle =
        typeof(SqliteConnection).GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance);

    public void Dispose()
    {
        var handle = NativeHandle?.GetValue(connection) as TursoDatabaseHandle;
        try
        {
            handle?.Deinitialize();
            foreach (var command in _commands.Values.Concat(_tableCommands.Values))
            {
                command.Dispose();
            }
        }
        finally
        {
            try
            {
                connection.Dispose();
            }
            finally
            {
                handle?.Dispose();
            }
        }
    }
}
