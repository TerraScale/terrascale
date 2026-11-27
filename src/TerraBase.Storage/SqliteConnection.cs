using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Utf8Text = System.Text.Unicode.Utf8;

namespace TerraBase.Storage;

internal sealed record SqliteOpenOptions(string Path, bool ReadOnly, ReadOnlyMemory<byte> RawKey, int CacheSizeKib, Ulid DatabaseId, DeltaColumnRegistry DeltaColumns)
{
    public TimeSpan StatementTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    public bool BackupDestination { get; init; }
}

internal sealed record CachedUserStatement(
    string Sql,
    nint Handle,
    SqlColumn[] Columns,
    bool ReadOnly,
    bool IsExplain,
    string[] ReadTables,
    string[] WrittenTables,
    bool HasWriterAnalysis,
    string AnalyzedSql,
    bool IsInsertOrReplace,
    StatementAnnotation? Annotation);

internal readonly union SqliteOpenResult(SqliteConnection, SqliteStatus);

internal sealed unsafe partial class SqliteConnection : IDisposable
{
    private const int MaxCachedStatements = 256;
    private const int MaxCachedUserStatements = 256;
    private const int ControlSqlCount = 7;

    private static readonly SqlColumn[] EmptyColumns = [];
    private static readonly IReadOnlyList<IReadOnlyList<SqlValue>> EmptyRows = [];
    private static readonly string[] EmptyTableNames = [];
    private static readonly SqliteStatus ResultTooLarge = new(SqliteNative.TooBig, "a result holds at most 4 MiB");
    private static readonly SqliteStatus TooManyRows = new(SqliteNative.TooBig, "a result holds at most 10000 rows");

    private readonly Dictionary<string, nint> _statements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<CachedUserStatement>> _userStatements = new(StringComparer.Ordinal);
    private readonly LinkedList<CachedUserStatement> _userLru = [];
    private readonly nint[] _controlStatements = new nint[ControlSqlCount];
    private readonly Dictionary<string, TableSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _handleGate = new();
    private readonly SqliteOpenOptions _options;
    private GCHandle<SqliteConnection> _self;
    private GCHandle<SnowflakeGenerator> _snowflake;
    private StatementInfo? _collecting;
    private CancellationToken _workCancellation;
    private long _workStarted;
    private bool _userMode;
    private bool _limitsInUserMode;
    private bool _applyMode;
    private bool _applyModeDirty;
    private long _schemaCookie = -1;
    private int _deltaVersion = -1;
    private long _userCacheCookie = -1;
    private int _userCacheDeltaVersion = -1;
    private bool _userCacheCheckedInTx;
    private nint _schemaCookieStatement;
    private nint _activeUserStatement;
    private bool _activeUserStatementEvicted;
    private nint _db;

    private SqliteConnection(nint db, SqliteOpenOptions options)
    {
        _db = db;
        _options = options;
        _self = new GCHandle<SqliteConnection>(this);
    }

    public bool HasUserLimits => _options.DatabaseId != Ulid.Empty;

    public bool InTransaction => SqliteNative.GetAutocommit(Handle) == 0;

    public PreupdateCapture? Capture { get; private set; }

    internal nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_db == 0, this);
            return _db;
        }
    }

    public static SqliteOpenResult Open(SqliteOpenOptions options)
    {
        SqliteLibrary.EnsureInitialized();
        var flags = (options.ReadOnly ? SqliteNative.OpenReadOnly : SqliteNative.OpenReadWrite | SqliteNative.OpenCreate)
            | SqliteNative.OpenNoMutex
            | SqliteNative.OpenExtendedResultCodes;
        nint db = 0;
        Span<byte> pathBuffer = stackalloc byte[256];
        var path = Utf8Z(options.Path, pathBuffer, out var rentedPath);
        int rc;
        fixed (byte* pathPointer = path)
        {
            rc = SqliteNative.OpenV2(pathPointer, &db, flags, null);
        }

        if (rentedPath is not null)
        {
            ArrayPool<byte>.Shared.Return(rentedPath);
        }

        if (rc != SqliteNative.Ok)
        {
            var message = db != 0 ? SqliteNative.ReadUtf8(SqliteNative.ErrMsg(db)) : SqliteNative.ReadUtf8(SqliteNative.ErrStr(rc));
            if (db != 0)
            {
                _ = SqliteNative.CloseV2(db);
            }

            return new SqliteStatus(rc, message);
        }

        var connection = new SqliteConnection(db, options);
        var status = connection.Configure();
        if (!status.IsOk)
        {
            connection.Dispose();
            return status;
        }

        return connection;
    }

    public static SqliteConnection? OpenOrNull(SqliteOpenOptions options) => Opened(Open(options));

    public static SqliteConnection? Opened(SqliteOpenResult result) => result switch
    {
        SqliteConnection connection => connection,
        SqliteStatus => null,
    };

    public void Interrupt()
    {
        lock (_handleGate)
        {
            if (_db != 0)
            {
                SqliteNative.InterruptQuery(_db);
            }
        }
    }

    public PreupdateCapture AttachCapture()
    {
        ObjectDisposedException.ThrowIf(_db == 0, this);
        if (Capture is not null || _options.ReadOnly)
        {
            throw new InvalidOperationException("A capture is already attached or the connection is read-only.");
        }

        Capture = new PreupdateCapture(this);
        return Capture;
    }

    public SqliteStatus RegisterSnowflake(SnowflakeGenerator generator)
    {
        if (_snowflake.IsAllocated)
        {
            return SqliteStatus.Success;
        }

        _snowflake = new GCHandle<SnowflakeGenerator>(generator);
        var name = "_tb_snowflake\0"u8;
        int rc;
        fixed (byte* namePointer = name)
        {
            rc = SqliteNative.CreateFunctionV2(Handle, namePointer, 0, SqliteNative.Utf8 | SqliteNative.Innocuous, GCHandle<SnowflakeGenerator>.ToIntPtr(_snowflake), &OnSnowflake, 0, 0, 0);
        }

        return Status(rc);
    }

    public SqliteStatus SetApplyMode(bool applying)
    {
        if (_applyMode == applying && !_applyModeDirty)
        {
            return SqliteStatus.Success;
        }

        if (InTransaction)
        {
            return new SqliteStatus(SqliteNative.Misuse, "the writer cannot change apply mode inside a transaction");
        }

        _applyModeDirty = true;
        var status = SetFlag(SqliteNative.ConfigEnableTrigger, applying ? 0 : 1);
        if (status.IsOk)
        {
            status = SetFlag(SqliteNative.ConfigDefensive, applying ? 0 : 1);
        }

        if (status.IsOk)
        {
            status = Exec(applying ? "PRAGMA foreign_keys = OFF;" : "PRAGMA foreign_keys = ON;");
        }

        if (!status.IsOk)
        {
            return status;
        }

        _applyMode = applying;
        _applyModeDirty = false;
        return SqliteStatus.Success;
    }

    public SqliteStatus Exec(string sql)
    {
        if (sql == "ROLLBACK TO _tb_item; RELEASE _tb_item;")
        {
            var rolled = ExecControl(6);
            return rolled.IsOk ? ExecControl(5) : rolled;
        }

        var control = ControlSqlIndex(sql);
        if (control >= 0)
        {
            return ExecControl(control);
        }

        _userCacheCheckedInTx = false;
        EnsureEngineLimits();
        Span<byte> stackBuffer = stackalloc byte[256];
        var bytes = Utf8Z(sql, stackBuffer, out var rented);
        byte* error = null;
        int rc;
        fixed (byte* sqlPointer = bytes)
        {
            rc = SqliteNative.Exec(Handle, sqlPointer, 0, 0, &error);
        }

        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        if (rc == SqliteNative.Ok)
        {
            return SqliteStatus.Success;
        }

        var status = Status(rc);
        if (error is not null)
        {
            status = status with { Message = SqliteNative.ReadUtf8(error) };
            SqliteNative.Free(error);
        }

        return status;
    }

    private static int ControlSqlIndex(string sql) => sql switch
    {
        "BEGIN;" => 0,
        "BEGIN IMMEDIATE;" => 1,
        "COMMIT;" => 2,
        "ROLLBACK;" => 3,
        "SAVEPOINT _tb_item;" => 4,
        "RELEASE _tb_item;" => 5,
        "ROLLBACK TO _tb_item;" => 6,
        _ => -1,
    };

    private static ReadOnlySpan<byte> ControlSqlUtf8(int index) => index switch
    {
        0 => "BEGIN;"u8,
        1 => "BEGIN IMMEDIATE;"u8,
        2 => "COMMIT;"u8,
        3 => "ROLLBACK;"u8,
        4 => "SAVEPOINT _tb_item;"u8,
        5 => "RELEASE _tb_item;"u8,
        6 => "ROLLBACK TO _tb_item;"u8,
        _ => throw new UnreachableException(),
    };

    private SqliteStatus ExecControl(int index)
    {
        if (index is 2 or 3)
        {
            _userCacheCheckedInTx = false;
        }

        var statement = _controlStatements[index];
        if (statement == 0)
        {
            var utf8 = ControlSqlUtf8(index);
            byte* tail = null;
            int rc;
            fixed (byte* pointer = utf8)
            {
                rc = SqliteNative.PrepareV3(Handle, pointer, utf8.Length, SqliteNative.PreparePersistent, &statement, &tail);
            }

            if (rc != SqliteNative.Ok)
            {
                return Status(rc);
            }

            _controlStatements[index] = statement;
        }

        var step = SqliteNative.Step(statement);
        if (step is SqliteNative.Done or SqliteNative.Row)
        {
            var reset = SqliteNative.Reset(statement);
            return reset == SqliteNative.Ok ? SqliteStatus.Success : Status(reset);
        }

        var status = Status(step);
        _ = SqliteNative.Reset(statement);
        return status;
    }

    public SqliteStatus Begin(string sql)
    {
        var cleared = RollbackOpenTransaction();
        return cleared.IsOk ? Exec(sql) : cleared;
    }

    public SqliteStatus RollbackOpenTransaction()
    {
        if (!InTransaction)
        {
            return SqliteStatus.Success;
        }

        var rolledBack = Exec("ROLLBACK;");
        InvalidateSchemaCache();
        if (!InTransaction)
        {
            return SqliteStatus.Success;
        }

        return rolledBack.IsOk ? new SqliteStatus(SqliteNative.Busy, "an earlier transaction is still open") : rolledBack;
    }

    public SqliteStatus Run(string sql, params ReadOnlySpan<SqlValue> parameters)
    {
        EnsureEngineLimits();
        var status = Rent(sql, out var statement, out var cached);
        if (!status.IsOk)
        {
            return status;
        }

        try
        {
            status = Bind(statement, parameters);
            while (status.IsOk)
            {
                var rc = SqliteNative.Step(statement);
                if (rc == SqliteNative.Done)
                {
                    break;
                }

                if (rc != SqliteNative.Row)
                {
                    status = Status(rc);
                }
            }
        }
        finally
        {
            Return(statement, cached);
        }

        return status;
    }

    public SqliteStatus Query(string sql, List<SqlValue[]> rows, params ReadOnlySpan<SqlValue> parameters)
    {
        EnsureEngineLimits();
        var status = Rent(sql, out var statement, out var cached);
        if (!status.IsOk)
        {
            return status;
        }

        try
        {
            status = Bind(statement, parameters);
            while (status.IsOk)
            {
                var rc = SqliteNative.Step(statement);
                if (rc == SqliteNative.Done)
                {
                    break;
                }

                if (rc != SqliteNative.Row)
                {
                    status = Status(rc);
                    break;
                }

                status = ReadRow(statement, out var row);
                if (status.IsOk)
                {
                    rows.Add(row);
                }
            }
        }
        finally
        {
            Return(statement, cached);
        }

        return status;
    }

    public SqliteStatus QueryFirst(string sql, out SqlValue[]? row, params ReadOnlySpan<SqlValue> parameters)
    {
        row = null;
        EnsureEngineLimits();
        var status = Rent(sql, out var statement, out var cached);
        if (!status.IsOk)
        {
            return status;
        }

        try
        {
            status = Bind(statement, parameters);
            if (status.IsOk)
            {
                var rc = SqliteNative.Step(statement);
                if (rc == SqliteNative.Row)
                {
                    status = ReadRow(statement, out var read);
                    row = status.IsOk ? read : null;
                }
                else if (rc != SqliteNative.Done)
                {
                    status = Status(rc);
                }
            }
        }
        finally
        {
            Return(statement, cached);
        }

        return status;
    }

    public SqliteStatus PrepareUser(ReadOnlySpan<byte> sql, SqlRuleScope? scope, StatementInfo info, out nint statement, out int consumed, bool persistent = false)
    {
        info.Reset();
        if (scope is not null)
        {
            var scoped = BindScope(scope, info);
            if (!scoped.IsOk)
            {
                statement = 0;
                consumed = sql.Length;
                return scoped;
            }
        }

        nint handle = 0;
        byte* tail = null;
        int rc;
        _collecting = info;
        EnterUserMode();
        try
        {
            fixed (byte* sqlPointer = sql)
            {
                rc = SqliteNative.PrepareV3(Handle, sqlPointer, sql.Length, persistent ? SqliteNative.PreparePersistent : 0, &handle, &tail);
                consumed = tail is null ? sql.Length : (int)(tail - sqlPointer);
            }
        }
        finally
        {
            LeaveUserMode();
            _collecting = null;
        }
        var status = rc == SqliteNative.Ok ? SqliteStatus.Success : UserStatus(Status(rc), info);
        if (handle != 0 || !status.IsOk)
        {
            var policy = ValidatePreparedSql(rc == SqliteNative.Ok ? sql[..consumed] : sql);
            if (!policy.IsOk)
            {
                status = policy;
            }
        }

        if (!status.IsOk)
        {
            FinalizeStatement(handle);
            handle = 0;
        }

        statement = handle;
        return status;
    }

    public CachedUserStatement? TryGetCachedUserStatement(string sql)
    {
        if (_userStatements.Count == 0)
        {
            return null;
        }

        if (!_userCacheCheckedInTx || _options.DeltaColumns.Version != _userCacheDeltaVersion)
        {
            var status = ReadSchemaCookie(out var cookie);
            if (!status.IsOk || cookie != _userCacheCookie || _options.DeltaColumns.Version != _userCacheDeltaVersion)
            {
                InvalidateSchemaCache();
                return null;
            }

            if (InTransaction)
            {
                _userCacheCheckedInTx = true;
            }
        }

        if (!_userStatements.TryGetValue(sql, out var node))
        {
            return null;
        }

        if (!ReferenceEquals(node, _userLru.First))
        {
            _userLru.Remove(node);
            _userLru.AddFirst(node);
        }

        return node.Value;
    }

    public CachedUserStatement? CacheUserStatement(
        string sql,
        nint handle,
        StatementInfo info,
        bool hasWriterAnalysis,
        string analyzedSql,
        bool isInsertOrReplace,
        StatementAnnotation? annotation)
    {
        if (_userCacheCookie < 0)
        {
            var status = ReadSchemaCookie(out var cookie);
            if (!status.IsOk)
            {
                return null;
            }

            _userCacheCookie = cookie;
            _userCacheDeltaVersion = _options.DeltaColumns.Version;
            if (InTransaction)
            {
                _userCacheCheckedInTx = true;
            }
        }

        var entry = new CachedUserStatement(
            sql,
            handle,
            ReadColumns(handle),
            IsReadOnly(handle),
            IsExplain(handle),
            info.ReadTables.Count == 0 ? EmptyTableNames : [.. info.ReadTables],
            info.WrittenTables.Count == 0 ? EmptyTableNames : [.. info.WrittenTables],
            hasWriterAnalysis,
            analyzedSql,
            isInsertOrReplace,
            annotation);

        if (_userStatements.TryGetValue(sql, out var existing))
        {
            if (existing.Value.Handle != handle)
            {
                FinalizeStatement(existing.Value.Handle);
            }

            _userLru.Remove(existing);
            existing.Value = entry;
            _userLru.AddFirst(existing);
            return entry;
        }

        if (_userLru.Count >= MaxCachedUserStatements && _userLru.Last is { } oldest)
        {
            _userLru.RemoveLast();
            _ = _userStatements.Remove(oldest.Value.Sql);
            if (oldest.Value.Handle == _activeUserStatement && _activeUserStatement != 0)
            {
                _activeUserStatementEvicted = true;
            }
            else
            {
                FinalizeStatement(oldest.Value.Handle);
            }
        }

        var added = _userLru.AddFirst(entry);
        _userStatements[sql] = added;
        return entry;
    }

    public static void ResetCachedUserStatement(nint statement)
    {
        if (statement != 0)
        {
            _ = SqliteNative.Reset(statement);
            _ = SqliteNative.ClearBindings(statement);
        }
    }

    public SqliteStatus StepCachedUser(CachedUserStatement cached, StatementInfo info, out SqlResultSet result, bool limitResults, CancellationToken cancellationToken)
    {
        _activeUserStatement = cached.Handle;
        _activeUserStatementEvicted = false;
        try
        {
            return StepUserCore(cached.Handle, info, cached.Columns, cached.ReadOnly, out result, limitResults, cancellationToken);
        }
        finally
        {
            var evicted = _activeUserStatementEvicted;
            _activeUserStatement = 0;
            _activeUserStatementEvicted = false;
            if (evicted)
            {
                FinalizeStatement(cached.Handle);
            }
            else
            {
                _ = SqliteNative.Reset(cached.Handle);
                _ = SqliteNative.ClearBindings(cached.Handle);
            }
        }
    }

    internal static bool IsTrailingEmpty(ReadOnlySpan<byte> tail)
    {
        foreach (var value in tail)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)';'))
            {
                return false;
            }
        }

        return true;
    }

    private void MarkUserCacheValidated(long cookie, int deltaVersion)
    {
        _userCacheCookie = cookie;
        _userCacheDeltaVersion = deltaVersion;
        if (InTransaction)
        {
            _userCacheCheckedInTx = true;
        }
    }

    private void ClearStatementCaches()
    {
        _userCacheCookie = -1;
        _userCacheDeltaVersion = -1;
        _userCacheCheckedInTx = false;
        foreach (var statement in _statements.Values)
        {
            _ = SqliteNative.Finalize(statement);
        }

        _statements.Clear();
        foreach (var entry in _userLru)
        {
            if (entry.Handle == _activeUserStatement && _activeUserStatement != 0)
            {
                _activeUserStatementEvicted = true;
            }
            else
            {
                _ = SqliteNative.Finalize(entry.Handle);
            }
        }

        _userLru.Clear();
        _userStatements.Clear();
    }

    private void EnterUserMode()
    {
        _userMode = true;
        EnsureUserLimits();
    }

    private void LeaveUserMode() => _userMode = false;

    private void EnsureUserLimits()
    {
        if (HasUserLimits && !_limitsInUserMode)
        {
            SqliteLimits.ApplyUser(Handle);
            _limitsInUserMode = true;
        }
    }

    private void EnsureEngineLimits()
    {
        if (HasUserLimits && _limitsInUserMode && !_userMode)
        {
            SqliteLimits.ApplyEngine(Handle);
            _limitsInUserMode = false;
        }
    }

    private SqliteStatus BindScope(SqlRuleScope scope, StatementInfo info)
    {
        if (scope.SchemaVersion is { } checkedVersion)
        {
            var version = ReadSchemaCookie(out var live);
            if (!version.IsOk)
            {
                return version;
            }

            if (live != checkedVersion)
            {
                return new SqliteStatus(SqliteNative.Schema, "the schema changed after the rules were checked; retry the statement");
            }
        }

        List<SqlValue[]> rows = [];
        var status = Query("SELECT type, name FROM main.sqlite_schema WHERE type IN ('view', 'trigger')", rows);
        if (!status.IsOk)
        {
            return status;
        }

        HashSet<string> views = new(AsciiIgnoreCaseComparer.Instance);
        HashSet<string> triggers = new(AsciiIgnoreCaseComparer.Instance);
        foreach (var row in rows)
        {
            if (row is [var type, var name] && type.TryGetText(out var kind) && name.TryGetText(out var text) && text is not null)
            {
                _ = (kind == "view" ? views : triggers).Add(text);
            }
        }

        if (triggers.Overlaps(scope.Ctes) || scope.Guards.Keys.Any(triggers.Contains))
        {
            return new SqliteStatus(SqliteNative.Auth, "a common table expression name matches a trigger; rename it");
        }

        views.IntersectWith(scope.Tables.Keys);
        views.ExceptWith(triggers);
        info.Scope = scope;
        info.Views = views.ToFrozenSet(AsciiIgnoreCaseComparer.Instance);
        return SqliteStatus.Success;
    }

    public SqliteStatus BindUser(nint statement, SqlStatement source)
    {
        EnsureUserLimits();
        var count = SqliteNative.BindParameterCount(statement);
        var positional = Math.Min(count, source.Positional.Count);
        for (var index = 0; index < positional; index++)
        {
            var status = BindOne(statement, index + 1, source.Positional[index]);
            if (!status.IsOk)
            {
                return ResourceStatus(status);
            }
        }

        foreach (var (name, value) in source.Named)
        {
            var position = ParameterIndex(statement, name);
            if (position == 0)
            {
                continue;
            }

            var status = BindOne(statement, position, value);
            if (!status.IsOk)
            {
                return ResourceStatus(status);
            }
        }

        return SqliteStatus.Success;
    }

    public SqliteStatus StepUser(nint statement, StatementInfo info, out SqlResultSet result, bool limitResults, CancellationToken cancellationToken) =>
        StepUserCore(statement, info, ReadColumns(statement), IsReadOnly(statement), out result, limitResults, cancellationToken);

    private SqliteStatus StepUserCore(
        nint statement,
        StatementInfo info,
        SqlColumn[] columns,
        bool readOnly,
        out SqlResultSet result,
        bool limitResults,
        CancellationToken cancellationToken)
    {
        List<IReadOnlyList<SqlValue>>? rows = null;
        var monitorProgress = cancellationToken.CanBeCanceled || _options.StatementTimeout != Timeout.InfiniteTimeSpan;
        _workCancellation = cancellationToken;
        _workStarted = monitorProgress && _options.StatementTimeout != Timeout.InfiniteTimeSpan ? Stopwatch.GetTimestamp() : 0;
        _collecting = info;
        EnterUserMode();
        SqliteStatus status;
        try
        {
            if (monitorProgress)
            {
                SqliteNative.SetProgressHandler(Handle, 1000, &OnProgress, GCHandle<SqliteConnection>.ToIntPtr(_self));
            }

            status = ReadUserRows(statement, ref rows, columns.Length, limitResults && HasUserLimits, monitorProgress);
        }
        finally
        {
            if (monitorProgress)
            {
                SqliteNative.SetProgressHandler(Handle, 0, null, 0);
            }

            LeaveUserMode();
            _collecting = null;
            _workCancellation = default;
            info.Scope = null;
        }

        result = new SqlResultSet(columns, rows ?? EmptyRows, readOnly ? 0 : SqliteNative.Changes64(_db), SqliteNative.LastInsertRowId(_db));
        return status.Code switch
        {
            SqliteNative.Interrupt when !cancellationToken.IsCancellationRequested => TimedOut,
            SqliteNative.ConstraintUnique when ViolatesPrimaryKey(status.Message) => status with { Code = SqliteNative.ConstraintPrimaryKey },
            _ => UserStatus(status, info),
        };
    }

    private static SqlColumn[] ReadColumns(nint statement)
    {
        var columnCount = SqliteNative.ColumnCount(statement);
        if (columnCount == 0)
        {
            return EmptyColumns;
        }

        var columns = new SqlColumn[columnCount];
        for (var index = 0; index < columnCount; index++)
        {
            columns[index] = new SqlColumn(
                SqliteNative.ReadUtf8(SqliteNative.ColumnName(statement, index)),
                SqliteNative.ReadOptionalUtf8(SqliteNative.ColumnDeclType(statement, index)));
        }

        return columns;
    }

    private SqliteStatus ReadUserRows(nint statement, ref List<IReadOnlyList<SqlValue>>? rows, int columnCount, bool limitResults, bool monitorProgress)
    {
        var remainingBytes = SqlLimits.MaxResultBytes;
        while (!monitorProgress || !IsWorkCanceled())
        {
            var rc = SqliteNative.Step(statement);
            if (Capture is { Fault.IsOk: false } capture)
            {
                return capture.Fault;
            }

            if (rc == SqliteNative.Done)
            {
                return SqliteStatus.Success;
            }

            if (rc != SqliteNative.Row)
            {
                return Status(rc);
            }

            if (limitResults && rows is { Count: SqlLimits.MaxResultRows })
            {
                return TooManyRows;
            }

            var read = ReadUserRow(statement, columnCount, limitResults, ref remainingBytes, out var row);
            if (!read.IsOk)
            {
                return read;
            }

            rows ??= [];
            rows.Add(row);
        }

        return Canceled;
    }

    private static SqliteStatus ReadUserRow(nint statement, int columnCount, bool limitResults, ref int remainingBytes, out SqlValue[] row)
    {
        row = columnCount == 0 ? [] : new SqlValue[columnCount];
        for (var index = 0; index < columnCount; index++)
        {
            switch (SqliteNative.ColumnType(statement, index))
            {
                case SqliteNative.TypeInteger:
                    if (limitResults && (remainingBytes -= sizeof(long)) < 0)
                    {
                        return ResultTooLarge;
                    }

                    row[index] = SqlValue.FromIntegral(SqliteNative.ColumnInt64(statement, index));
                    break;
                case SqliteNative.TypeFloat:
                    if (limitResults && (remainingBytes -= sizeof(long)) < 0)
                    {
                        return ResultTooLarge;
                    }

                    var real = SqliteNative.ColumnDouble(statement, index);
                    if (!double.IsFinite(real))
                    {
                        return SqliteStatus.NonFiniteReal;
                    }

                    row[index] = SqlValue.FromReal(real);
                    break;
                case SqliteNative.TypeText:
                    var textLength = SqliteNative.ColumnBytes(statement, index);
                    if (limitResults && (remainingBytes -= Math.Max(1, textLength)) < 0)
                    {
                        return ResultTooLarge;
                    }

                    if (textLength == 0)
                    {
                        row[index] = SqlValue.FromText(string.Empty);
                        break;
                    }

                    var textPointer = SqliteNative.ColumnText(statement, index);
                    var textBytes = textPointer is null ? [] : new ReadOnlySpan<byte>(textPointer, textLength);
                    if (!Utf8Text.IsValid(textBytes))
                    {
                        return SqliteStatus.InvalidUtf8;
                    }

                    row[index] = SqlValue.FromText(Encoding.UTF8.GetString(textBytes));
                    break;
                case SqliteNative.TypeBlob:
                    var blobLength = SqliteNative.ColumnBytes(statement, index);
                    if (limitResults && (remainingBytes -= Math.Max(1, blobLength)) < 0)
                    {
                        return ResultTooLarge;
                    }

                    if (blobLength == 0)
                    {
                        row[index] = SqlValue.FromBlob(ReadOnlyMemory<byte>.Empty);
                        break;
                    }

                    var blobPointer = SqliteNative.ColumnBlob(statement, index);
                    var blob = blobPointer is null ? [] : new ReadOnlySpan<byte>(blobPointer, blobLength).ToArray();
                    row[index] = SqlValue.FromBlob(blob);
                    break;
                default:
                    if (limitResults && (remainingBytes -= 1) < 0)
                    {
                        return ResultTooLarge;
                    }

                    row[index] = SqlValue.Null;
                    break;
            }
        }

        return SqliteStatus.Success;
    }

    private bool IsWorkCanceled() =>
        _workCancellation.IsCancellationRequested || (_options.StatementTimeout != Timeout.InfiniteTimeSpan
            && Stopwatch.GetElapsedTime(_workStarted) >= _options.StatementTimeout);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnProgress(nint context)
    {
        try
        {
            return GCHandle<SqliteConnection>.FromIntPtr(context).Target.IsWorkCanceled() ? 1 : 0;
        }
        catch
        {
            return 1;
        }
    }

    private bool ViolatesPrimaryKey(string message)
    {
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
        {
            return false;
        }

        var names = message[(colon + 2)..];
        var end = names.IndexOf(" (", StringComparison.Ordinal);
        string? table = null;
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        foreach (var name in (end < 0 ? names : names[..end]).Split(", "))
        {
            var dot = name.LastIndexOf('.');
            if (dot <= 0 || (table is not null && !string.Equals(table, name[..dot], StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            table = name[..dot];
            _ = columns.Add(name[(dot + 1)..]);
        }

        if (table is null)
        {
            return false;
        }

        var lookup = Schema(table);
        return lookup switch
        {
            TableSchema { UsesImplicitRowId: false } schema => columns.SetEquals(schema.PrimaryKeyColumnNames),
            TableSchema => false,
            TableNotFound => false,
            SqliteStatus => false,
        };
    }

    public static bool IsReadOnly(nint statement) => SqliteNative.StatementReadOnly(statement) != 0;

    public static bool IsExplain(nint statement) => SqliteNative.StatementIsExplain(statement) != 0;

    public static void FinalizeStatement(nint statement)
    {
        if (statement != 0)
        {
            _ = SqliteNative.Finalize(statement);
        }
    }

    public SqliteStatus ExplainQueryPlan(string sql, List<string> details)
    {
        EnsureEngineLimits();
        var bytes = Encoding.UTF8.GetBytes(string.Concat("EXPLAIN QUERY PLAN ", sql));
        nint statement = 0;
        byte* tail = null;
        int rc;
        fixed (byte* sqlPointer = bytes)
        {
            rc = SqliteNative.PrepareV3(Handle, sqlPointer, bytes.Length, 0, &statement, &tail);
        }

        if (rc != SqliteNative.Ok)
        {
            return Status(rc);
        }

        var status = SqliteStatus.Success;
        while (statement != 0)
        {
            rc = SqliteNative.Step(statement);
            if (rc == SqliteNative.Done)
            {
                break;
            }

            if (rc != SqliteNative.Row)
            {
                status = Status(rc);
                break;
            }

            details.Add(SqliteNative.ReadUtf8(SqliteNative.ColumnText(statement, 3)));
        }

        FinalizeStatement(statement);
        return status;
    }

    public SqliteStatus BackupTo(SqliteConnection destination)
    {
        var main = "main\0"u8;
        nint backup;
        fixed (byte* name = main)
        {
            backup = SqliteNative.BackupInit(destination.Handle, name, Handle, name);
        }

        if (backup == 0)
        {
            return new SqliteStatus(SqliteNative.ExtendedErrCode(destination.Handle), SqliteNative.ReadUtf8(SqliteNative.ErrMsg(destination.Handle)));
        }

        var step = SqliteNative.BackupStep(backup, -1);
        var finish = SqliteNative.BackupFinish(backup);
        return step is SqliteNative.Done or SqliteNative.Ok && finish == SqliteNative.Ok
            ? SqliteStatus.Success
            : new SqliteStatus(SqliteNative.ExtendedErrCode(destination.Handle), SqliteNative.ReadUtf8(SqliteNative.ErrMsg(destination.Handle)));
    }

    public void Dispose()
    {
        lock (_handleGate)
        {
            var db = _db;
            if (db == 0)
            {
                return;
            }

            Capture?.Dispose();
            Capture = null;
            foreach (var statement in _statements.Values)
            {
                _ = SqliteNative.Finalize(statement);
            }

            _statements.Clear();
            foreach (var entry in _userLru)
            {
                _ = SqliteNative.Finalize(entry.Handle);
            }

            _userLru.Clear();
            _userStatements.Clear();
            for (var index = 0; index < _controlStatements.Length; index++)
            {
                if (_controlStatements[index] != 0)
                {
                    _ = SqliteNative.Finalize(_controlStatements[index]);
                    _controlStatements[index] = 0;
                }
            }

            if (_schemaCookieStatement != 0)
            {
                _ = SqliteNative.Finalize(_schemaCookieStatement);
                _schemaCookieStatement = 0;
            }

            _schemas.Clear();
            _ = SqliteNative.SetAuthorizer(db, null, 0);
            _db = 0;
            _ = SqliteNative.CloseV2(db);
        }

        _self.Dispose();
        if (_snowflake.IsAllocated)
        {
            _snowflake.Dispose();
        }
    }

    private static readonly SqliteStatus Canceled = new(SqliteNative.Interrupt, "canceling statement due to user request");

    private static readonly SqliteStatus TimedOut = new(SqliteNative.Interrupt, "canceling statement due to statement timeout");

    internal SqliteStatus Status(int rc)
    {
        if ((rc & 0xFF) == SqliteNative.Interrupt)
        {
            return Canceled;
        }

        var extended = SqliteNative.ExtendedErrCode(_db);
        var code = (extended & 0xFF) == (rc & 0xFF) ? extended : rc;
        return new SqliteStatus(code, SqliteNative.ReadUtf8(SqliteNative.ErrMsg(_db)));
    }

    private SqliteStatus Configure()
    {
        var status = Exec("PRAGMA cipher = 'chacha20';");
        if (!status.IsOk)
        {
            return status;
        }

        status = ApplyKey(_options.RawKey.Span);
        if (!status.IsOk)
        {
            return status;
        }

        status = Harden();
        if (!status.IsOk)
        {
            return status;
        }

        if (HasUserLimits)
        {
            status = SqliteLimits.Verify(_db);
            if (!status.IsOk)
            {
                return status;
            }
        }

        var pragmas = _options.ReadOnly
            ? string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = 5000; PRAGMA cache_size = -{_options.CacheSizeKib}; PRAGMA query_only = 1;")
            : _options.BackupDestination
                ? string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = 5000; PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA foreign_keys = ON; PRAGMA cache_size = -{_options.CacheSizeKib};")
                : string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA cache_size = -{_options.CacheSizeKib};");
        status = Exec(pragmas);
        if (!status.IsOk)
        {
            return status;
        }

        return QueryFirst("SELECT count(*) FROM sqlite_schema", out _);
    }

    private SqliteStatus Harden()
    {
        var status = SetFlag(SqliteNative.ConfigDefensive, 1);
        if (status.IsOk)
        {
            status = SetFlag(SqliteNative.ConfigTrustedSchema, 0);
        }

        if (status.IsOk)
        {
            status = SetFlag(SqliteNative.ConfigEnableLoadExtension, 0);
        }

        if (!status.IsOk)
        {
            return status;
        }

        var rc = SqliteNative.SetAuthorizer(_db, &OnAuthorize, GCHandle<SqliteConnection>.ToIntPtr(_self));
        return rc == SqliteNative.Ok ? SqliteStatus.Success : Status(rc);
    }

    private SqliteStatus SetFlag(int option, int value)
    {
        var applied = -1;
        var rc = SqliteNative.DbConfigFlag(Handle, option, value, &applied);
        if (rc != SqliteNative.Ok)
        {
            return Status(rc);
        }

        return applied == value
            ? SqliteStatus.Success
            : new SqliteStatus(SqliteNative.Error, string.Concat("sqlite configuration option ", option.ToString(CultureInfo.InvariantCulture), " was not applied"));
    }

    private SqliteStatus ApplyKey(ReadOnlySpan<byte> rawKey)
    {
        var prefix = "raw:bin:"u8;
        Span<byte> material = stackalloc byte[prefix.Length + DatabaseOpenRequest.RawKeyLength];
        prefix.CopyTo(material);
        rawKey.CopyTo(material[prefix.Length..]);
        var main = "main\0"u8;
        int rc;
        fixed (byte* keyPointer = material)
        fixed (byte* namePointer = main)
        {
            rc = SqliteNative.KeyV2(_db, namePointer, keyPointer, material.Length);
        }

        material.Clear();
        return rc == SqliteNative.Ok ? SqliteStatus.Success : Status(rc);
    }

    private SqliteStatus Rent(string sql, out nint statement, out bool cached)
    {
        if (_statements.TryGetValue(sql, out statement))
        {
            cached = true;
            return SqliteStatus.Success;
        }

        cached = _statements.Count < MaxCachedStatements;
        var bytes = Encoding.UTF8.GetBytes(sql);
        nint handle = 0;
        byte* tail = null;
        int rc;
        fixed (byte* sqlPointer = bytes)
        {
            rc = SqliteNative.PrepareV3(Handle, sqlPointer, bytes.Length, cached ? SqliteNative.PreparePersistent : 0, &handle, &tail);
        }

        statement = handle;
        if (rc != SqliteNative.Ok)
        {
            return Status(rc);
        }

        if (handle == 0)
        {
            return SqliteStatus.Fail("empty statement");
        }

        if (cached)
        {
            _statements[sql] = handle;
        }

        return SqliteStatus.Success;
    }

    private static void Return(nint statement, bool cached)
    {
        if (cached)
        {
            _ = SqliteNative.Reset(statement);
            _ = SqliteNative.ClearBindings(statement);
            return;
        }

        _ = SqliteNative.Finalize(statement);
    }

    private SqliteStatus Bind(nint statement, ReadOnlySpan<SqlValue> parameters)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            var status = BindOne(statement, index + 1, parameters[index]);
            if (!status.IsOk)
            {
                return status;
            }
        }

        return SqliteStatus.Success;
    }

    private SqliteStatus BindOne(nint statement, int position, SqlValue value)
    {
        int rc;
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                rc = SqliteNative.BindNull(statement, position);
                break;
            case SqlValueKind.Integral:
                _ = value.TryGetIntegral(out var integral);
                rc = SqliteNative.BindInt64(statement, position, integral);
                break;
            case SqlValueKind.Real:
                _ = value.TryGetReal(out var real);
                rc = SqliteNative.BindDouble(statement, position, real);
                break;
            case SqlValueKind.Text:
                _ = value.TryGetText(out var text);
                rc = BindText(statement, position, text ?? throw new UnreachableException());
                break;
            case SqlValueKind.Blob:
                _ = value.TryGetBlob(out var blob);
                rc = BindBlob(statement, position, blob.Span);
                break;
            default:
                throw new UnreachableException();
        }

        return rc == SqliteNative.Ok ? SqliteStatus.Success : Status(rc);
    }

    private static int BindText(nint statement, int position, string text)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (maxBytes <= 256)
        {
            Span<byte> buffer = stackalloc byte[256];
            var count = Encoding.UTF8.GetBytes(text, buffer);
            fixed (byte* pointer = buffer)
            {
                return SqliteNative.BindText(statement, position, pointer, count, SqliteNative.Transient);
            }
        }

        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        var written = Encoding.UTF8.GetBytes(text, rented);
        int rc;
        fixed (byte* textPointer = rented)
        {
            rc = SqliteNative.BindText(statement, position, textPointer, written, SqliteNative.Transient);
        }

        ArrayPool<byte>.Shared.Return(rented);
        return rc;
    }

    private static int BindBlob(nint statement, int position, ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty)
        {
            return SqliteNative.BindZeroBlob(statement, position, 0);
        }

        fixed (byte* blobPointer = blob)
        {
            return SqliteNative.BindBlob(statement, position, blobPointer, blob.Length, SqliteNative.Transient);
        }
    }

    private static int ParameterIndex(nint statement, string name)
    {
        if (name.Length > 0 && name[0] is ':' or '@' or '$' or '#' or '?')
        {
            return ParameterIndexExact(statement, 0, name);
        }

        foreach (var prefix in ":@$#"u8)
        {
            var index = ParameterIndexExact(statement, prefix, name);
            if (index != 0)
            {
                return index;
            }
        }

        return 0;
    }

    private static int ParameterIndexExact(nint statement, byte prefix, string name)
    {
        var extra = prefix == 0 ? 1 : 2;
        var maxBytes = Encoding.UTF8.GetMaxByteCount(name.Length) + extra;
        if (maxBytes <= 256)
        {
            Span<byte> buffer = stackalloc byte[256];
            var offset = 0;
            if (prefix != 0)
            {
                buffer[0] = prefix;
                offset = 1;
            }

            var written = Encoding.UTF8.GetBytes(name, buffer[offset..]);
            buffer[offset + written] = 0;
            fixed (byte* namePointer = buffer)
            {
                return SqliteNative.BindParameterIndex(statement, namePointer);
            }
        }

        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        var start = 0;
        if (prefix != 0)
        {
            rented[0] = prefix;
            start = 1;
        }

        var length = Encoding.UTF8.GetBytes(name, rented.AsSpan(start));
        rented[start + length] = 0;
        int index;
        fixed (byte* namePointer = rented)
        {
            index = SqliteNative.BindParameterIndex(statement, namePointer);
        }

        ArrayPool<byte>.Shared.Return(rented);
        return index;
    }

    private static SqliteStatus ReadRow(nint statement, out SqlValue[] row)
    {
        var count = SqliteNative.ColumnCount(statement);
        row = new SqlValue[count];
        for (var index = 0; index < count; index++)
        {
            var read = SqliteNative.ReadColumn(statement, index, out row[index]);
            if (!read.IsOk)
            {
                return read;
            }
        }

        return SqliteStatus.Success;
    }

    private static ReadOnlySpan<byte> Utf8Z(string value, Span<byte> stackBuffer, out byte[]? rented)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length) + 1;
        if (maxBytes <= stackBuffer.Length)
        {
            rented = null;
            var written = Encoding.UTF8.GetBytes(value, stackBuffer);
            stackBuffer[written] = 0;
            return stackBuffer[..(written + 1)];
        }

        rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        var length = Encoding.UTF8.GetBytes(value, rented);
        rented[length] = 0;
        return rented.AsSpan(0, length + 1);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSnowflake(nint context, int argumentCount, nint* arguments)
    {
        try
        {
            var generator = GCHandle<SnowflakeGenerator>.FromIntPtr(SqliteNative.UserData(context)).Target;
            SqliteNative.ResultInt64(context, generator.Next());
        }
        catch
        {
            var message = "snowflake generation failed"u8;
            fixed (byte* pointer = message)
            {
                SqliteNative.ResultError(context, pointer, message.Length);
            }
        }
    }
}
