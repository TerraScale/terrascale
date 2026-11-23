using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace TerraBase.Storage;

internal sealed record SqliteOpenOptions(string Path, bool ReadOnly, ReadOnlyMemory<byte> RawKey, int CacheSizeKib, Ulid DatabaseId, DeltaColumnRegistry DeltaColumns);

internal readonly union SqliteOpenResult(SqliteConnection, SqliteStatus);

internal sealed unsafe partial class SqliteConnection : IDisposable
{
    private const int MaxCachedStatements = 256;

    private readonly Dictionary<string, nint> _statements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TableSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly SqliteOpenOptions _options;
    private GCHandle<SqliteConnection> _self;
    private GCHandle<SnowflakeGenerator> _snowflake;
    private StatementInfo? _collecting;
    private bool _userMode;
    private bool _applyMode;
    private long _schemaCookie = -1;
    private int _deltaVersion = -1;
    private nint _db;

    private SqliteConnection(nint db, SqliteOpenOptions options)
    {
        _db = db;
        _options = options;
        _self = new GCHandle<SqliteConnection>(this);
    }

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
        SqliteLibrary.EnsureRegistered();
        var flags = (options.ReadOnly ? SqliteNative.OpenReadOnly : SqliteNative.OpenReadWrite | SqliteNative.OpenCreate)
            | SqliteNative.OpenNoMutex
            | SqliteNative.OpenExtendedResultCodes;
        nint db = 0;
        var path = Utf8Z(options.Path);
        int rc;
        fixed (byte* pathPointer = path)
        {
            rc = SqliteNative.OpenV2(pathPointer, &db, flags, null);
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
            rc = SqliteNative.CreateFunctionV2(Handle, namePointer, 0, SqliteNative.Utf8, GCHandle<SnowflakeGenerator>.ToIntPtr(_snowflake), &OnSnowflake, 0, 0, 0);
        }

        return Status(rc);
    }

    public SqliteStatus SetApplyMode(bool applying)
    {
        if (_applyMode == applying)
        {
            return SqliteStatus.Success;
        }

        int result;
        var rc = SqliteNative.DbConfigFlag(Handle, SqliteNative.ConfigEnableTrigger, applying ? 0 : 1, &result);
        if (rc == SqliteNative.Ok)
        {
            rc = SqliteNative.DbConfigFlag(Handle, SqliteNative.ConfigDefensive, applying ? 0 : 1, &result);
        }

        if (rc != SqliteNative.Ok)
        {
            return Status(rc);
        }

        _applyMode = applying;
        return SqliteStatus.Success;
    }

    public SqliteStatus Exec(string sql)
    {
        var bytes = Utf8Z(sql);
        byte* error = null;
        int rc;
        fixed (byte* sqlPointer = bytes)
        {
            rc = SqliteNative.Exec(Handle, sqlPointer, 0, 0, &error);
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

    public SqliteStatus Run(string sql, params ReadOnlySpan<SqlValue> parameters)
    {
        var status = Rent(sql, out var statement, out var cached);
        if (!status.IsOk)
        {
            return status;
        }

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

        Return(statement, cached);
        return status;
    }

    public SqliteStatus Query(string sql, List<SqlValue[]> rows, params ReadOnlySpan<SqlValue> parameters)
    {
        var status = Rent(sql, out var statement, out var cached);
        if (!status.IsOk)
        {
            return status;
        }

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

            rows.Add(ReadRow(statement));
        }

        Return(statement, cached);
        return status;
    }

    public SqliteStatus QueryFirst(string sql, out SqlValue[]? row, params ReadOnlySpan<SqlValue> parameters)
    {
        row = null;
        var status = Rent(sql, out var statement, out var cached);
        if (!status.IsOk)
        {
            return status;
        }

        status = Bind(statement, parameters);
        if (status.IsOk)
        {
            var rc = SqliteNative.Step(statement);
            if (rc == SqliteNative.Row)
            {
                row = ReadRow(statement);
            }
            else if (rc != SqliteNative.Done)
            {
                status = Status(rc);
            }
        }

        Return(statement, cached);
        return status;
    }

    public SqliteStatus PrepareUser(ReadOnlySpan<byte> sql, StatementInfo info, out nint statement, out int consumed)
    {
        info.Reset();
        nint handle = 0;
        byte* tail = null;
        int rc;
        _collecting = info;
        _userMode = true;
        fixed (byte* sqlPointer = sql)
        {
            rc = SqliteNative.PrepareV3(Handle, sqlPointer, sql.Length, 0, &handle, &tail);
            consumed = tail is null ? sql.Length : (int)(tail - sqlPointer);
        }

        _userMode = false;
        _collecting = null;
        statement = handle;
        if (rc == SqliteNative.Ok)
        {
            return SqliteStatus.Success;
        }

        var status = Status(rc);
        return info.Denied is { } denied ? new SqliteStatus(SqliteNative.Auth, denied) : status;
    }

    public SqliteStatus BindUser(nint statement, SqlStatement source)
    {
        var count = SqliteNative.BindParameterCount(statement);
        var positional = Math.Min(count, source.Positional.Count);
        for (var index = 0; index < positional; index++)
        {
            var status = BindOne(statement, index + 1, source.Positional[index]);
            if (!status.IsOk)
            {
                return status;
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
                return status;
            }
        }

        return SqliteStatus.Success;
    }

    public SqliteStatus StepUser(nint statement, out SqlResultSet result)
    {
        var columnCount = SqliteNative.ColumnCount(statement);
        var columns = new SqlColumn[columnCount];
        for (var index = 0; index < columnCount; index++)
        {
            columns[index] = new SqlColumn(
                SqliteNative.ReadUtf8(SqliteNative.ColumnName(statement, index)),
                SqliteNative.ReadOptionalUtf8(SqliteNative.ColumnDeclType(statement, index)));
        }

        List<IReadOnlyList<SqlValue>> rows = [];
        var readOnly = SqliteNative.StatementReadOnly(statement) != 0;
        _userMode = true;
        var status = SqliteStatus.Success;
        while (true)
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

            rows.Add(ReadRow(statement));
        }

        _userMode = false;
        result = new SqlResultSet(columns, rows, readOnly ? 0 : SqliteNative.Changes64(_db), SqliteNative.LastInsertRowId(_db));
        return status;
    }

    public static bool IsReadOnly(nint statement) => SqliteNative.StatementReadOnly(statement) != 0;

    public static void FinalizeStatement(nint statement)
    {
        if (statement != 0)
        {
            _ = SqliteNative.Finalize(statement);
        }
    }

    public SqliteStatus ExplainQueryPlan(string sql, List<string> details)
    {
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
        if (_db == 0)
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
        _schemas.Clear();
        _ = SqliteNative.SetAuthorizer(_db, null, 0);
        _ = SqliteNative.CloseV2(_db);
        _db = 0;
        _self.Dispose();
        if (_snowflake.IsAllocated)
        {
            _snowflake.Dispose();
        }
    }

    internal SqliteStatus Status(int rc)
    {
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

        int ignored;
        _ = SqliteNative.DbConfigFlag(_db, SqliteNative.ConfigDefensive, 1, &ignored);
        _ = SqliteNative.SetAuthorizer(_db, &OnAuthorize, GCHandle<SqliteConnection>.ToIntPtr(_self));
        var pragmas = _options.ReadOnly
            ? string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = 5000; PRAGMA cache_size = -{_options.CacheSizeKib}; PRAGMA query_only = 1;")
            : string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA cache_size = -{_options.CacheSizeKib};");
        status = Exec(pragmas);
        if (!status.IsOk)
        {
            return status;
        }

        return QueryFirst("SELECT count(*) FROM sqlite_schema", out _);
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
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(maxBytes, 1));
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
        if (name.Length > 0 && name[0] is ':' or '@' or '$' or '?')
        {
            return ParameterIndexExact(statement, name);
        }

        foreach (var prefix in ":@$")
        {
            var index = ParameterIndexExact(statement, string.Concat(prefix.ToString(), name));
            if (index != 0)
            {
                return index;
            }
        }

        return 0;
    }

    private static int ParameterIndexExact(nint statement, string name)
    {
        var bytes = Utf8Z(name);
        fixed (byte* namePointer = bytes)
        {
            return SqliteNative.BindParameterIndex(statement, namePointer);
        }
    }

    private static SqlValue[] ReadRow(nint statement)
    {
        var count = SqliteNative.ColumnCount(statement);
        var row = new SqlValue[count];
        for (var index = 0; index < count; index++)
        {
            row[index] = SqliteNative.ReadColumn(statement, index);
        }

        return row;
    }

    private static byte[] Utf8Z(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSnowflake(nint context, int argumentCount, nint* arguments)
    {
        var generator = GCHandle<SnowflakeGenerator>.FromIntPtr(SqliteNative.UserData(context)).Target;
        SqliteNative.ResultInt64(context, generator.Next());
    }
}
