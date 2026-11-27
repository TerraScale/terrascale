using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Utf8Text = System.Text.Unicode.Utf8;

namespace TerraBase.Storage;

internal static unsafe partial class SqliteNative
{
    public const string LibraryName = "sqlite3mc";

    public const int Ok = 0;
    public const int Error = 1;
    public const int Internal = 2;
    public const int Perm = 3;
    public const int Abort = 4;
    public const int Busy = 5;
    public const int Locked = 6;
    public const int NoMem = 7;
    public const int ReadOnly = 8;
    public const int Interrupt = 9;
    public const int IoError = 10;
    public const int Corrupt = 11;
    public const int Full = 13;
    public const int CantOpen = 14;
    public const int Protocol = 15;
    public const int TooBig = 18;
    public const int Constraint = 19;
    public const int Mismatch = 20;
    public const int Misuse = 21;
    public const int NoLargeFile = 22;
    public const int Schema = 17;
    public const int Auth = 23;
    public const int Range = 25;
    public const int NotADatabase = 26;
    public const int Row = 100;
    public const int Done = 101;

    public const int ConstraintCheck = Constraint | (1 << 8);
    public const int ConstraintForeignKey = Constraint | (3 << 8);
    public const int ConstraintNotNull = Constraint | (5 << 8);
    public const int ConstraintPrimaryKey = Constraint | (6 << 8);
    public const int ConstraintUnique = Constraint | (8 << 8);

    public const int OpenReadOnly = 0x00000001;
    public const int OpenReadWrite = 0x00000002;
    public const int OpenCreate = 0x00000004;
    public const int OpenNoMutex = 0x00008000;
    public const int OpenExtendedResultCodes = 0x02000000;

    public const uint PreparePersistent = 0x01;

    public const int TypeInteger = 1;
    public const int TypeFloat = 2;
    public const int TypeText = 3;
    public const int TypeBlob = 4;
    public const int TypeNull = 5;

    public const int OpDelete = 9;
    public const int OpInsert = 18;
    public const int OpUpdate = 23;

    public const int AuthOk = 0;
    public const int AuthDeny = 1;
    public const int AuthIgnore = 2;

    public const int ActionCreateIndex = 1;
    public const int ActionCreateTable = 2;
    public const int ActionCreateTempIndex = 3;
    public const int ActionCreateTempTable = 4;
    public const int ActionCreateTempTrigger = 5;
    public const int ActionCreateTempView = 6;
    public const int ActionCreateTrigger = 7;
    public const int ActionCreateView = 8;
    public const int ActionDelete = 9;
    public const int ActionDropIndex = 10;
    public const int ActionDropTable = 11;
    public const int ActionDropTempIndex = 12;
    public const int ActionDropTempTable = 13;
    public const int ActionDropTempTrigger = 14;
    public const int ActionDropTempView = 15;
    public const int ActionDropTrigger = 16;
    public const int ActionDropView = 17;
    public const int ActionInsert = 18;
    public const int ActionPragma = 19;
    public const int ActionRead = 20;
    public const int ActionTransaction = 22;
    public const int ActionUpdate = 23;
    public const int ActionAttach = 24;
    public const int ActionDetach = 25;
    public const int ActionAlterTable = 26;
    public const int ActionReindex = 27;
    public const int ActionAnalyze = 28;
    public const int ActionCreateVirtualTable = 29;
    public const int ActionDropVirtualTable = 30;
    public const int ActionFunction = 31;
    public const int ActionSavepoint = 32;

    public const int LimitLength = 0;
    public const int LimitSqlLength = 1;
    public const int LimitExprDepth = 3;
    public const int LimitCompoundSelect = 4;
    public const int LimitVdbeOp = 5;
    public const int LimitFunctionArg = 6;
    public const int LimitAttached = 7;
    public const int LimitLikePatternLength = 8;
    public const int LimitVariableNumber = 9;
    public const int LimitTriggerDepth = 10;

    public const int ConfigEnableTrigger = 1003;
    public const int ConfigEnableLoadExtension = 1005;
    public const int ConfigDefensive = 1010;
    public const int ConfigTrustedSchema = 1017;
    public const int Utf8 = 1;
    public const int Innocuous = 0x000200000;

    public static readonly nint Transient = -1;

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_libversion")]
    public static partial byte* LibVersion();

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_soft_heap_limit64")]
    public static partial long SoftHeapLimit64(long limit);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_hard_heap_limit64")]
    public static partial long HardHeapLimit64(long limit);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_open_v2")]
    public static partial int OpenV2(byte* filename, nint* db, int flags, byte* vfs);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_close_v2")]
    public static partial int CloseV2(nint db);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_limit")]
    public static partial int Limit(nint db, int id, int value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_interrupt")]
    public static partial void InterruptQuery(nint db);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_progress_handler")]
    public static partial void SetProgressHandler(nint db, int instructions, delegate* unmanaged[Cdecl]<nint, int> callback, nint context);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_errmsg")]
    public static partial byte* ErrMsg(nint db);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_errstr")]
    public static partial byte* ErrStr(int resultCode);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_extended_errcode")]
    public static partial int ExtendedErrCode(nint db);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_exec")]
    public static partial int Exec(nint db, byte* sql, nint callback, nint argument, byte** errorMessage);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_free")]
    public static partial void Free(void* pointer);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_prepare_v3")]
    public static partial int PrepareV3(nint db, byte* sql, int byteCount, uint prepareFlags, nint* statement, byte** tail);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_step")]
    public static partial int Step(nint statement);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_reset")]
    public static partial int Reset(nint statement);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_clear_bindings")]
    public static partial int ClearBindings(nint statement);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_finalize")]
    public static partial int Finalize(nint statement);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_stmt_readonly")]
    [SuppressGCTransition]
    public static partial int StatementReadOnly(nint statement);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_stmt_isexplain")]
    [SuppressGCTransition]
    public static partial int StatementIsExplain(nint statement);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_bind_parameter_count")]
    [SuppressGCTransition]
    public static partial int BindParameterCount(nint statement);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_bind_parameter_index")]
    public static partial int BindParameterIndex(nint statement, byte* name);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_bind_null")]
    public static partial int BindNull(nint statement, int index);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_bind_int64")]
    public static partial int BindInt64(nint statement, int index, long value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_bind_double")]
    public static partial int BindDouble(nint statement, int index, double value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_bind_text")]
    public static partial int BindText(nint statement, int index, byte* value, int byteCount, nint destructor);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_bind_blob")]
    public static partial int BindBlob(nint statement, int index, void* value, int byteCount, nint destructor);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_bind_zeroblob")]
    public static partial int BindZeroBlob(nint statement, int index, int byteCount);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_column_count")]
    [SuppressGCTransition]
    public static partial int ColumnCount(nint statement);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_column_name")]
    public static partial byte* ColumnName(nint statement, int index);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_column_decltype")]
    public static partial byte* ColumnDeclType(nint statement, int index);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_column_type")]
    [SuppressGCTransition]
    public static partial int ColumnType(nint statement, int index);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_column_int64")]
    [SuppressGCTransition]
    public static partial long ColumnInt64(nint statement, int index);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_column_double")]
    [SuppressGCTransition]
    public static partial double ColumnDouble(nint statement, int index);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_column_text")]
    public static partial byte* ColumnText(nint statement, int index);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_column_blob")]
    public static partial void* ColumnBlob(nint statement, int index);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_column_bytes")]
    public static partial int ColumnBytes(nint statement, int index);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_changes64")]
    [SuppressGCTransition]
    public static partial long Changes64(nint db);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_last_insert_rowid")]
    [SuppressGCTransition]
    public static partial long LastInsertRowId(nint db);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_get_autocommit")]
    [SuppressGCTransition]
    public static partial int GetAutocommit(nint db);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_preupdate_hook")]
    public static partial nint PreupdateHook(
        nint db,
        delegate* unmanaged[Cdecl]<nint, nint, int, byte*, byte*, long, long, void> callback,
        nint context);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_preupdate_old")]
    public static partial int PreupdateOld(nint db, int index, nint* value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_preupdate_new")]
    public static partial int PreupdateNew(nint db, int index, nint* value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_preupdate_count")]
    [SuppressGCTransition]
    public static partial int PreupdateCount(nint db);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_preupdate_depth")]
    [SuppressGCTransition]
    public static partial int PreupdateDepth(nint db);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_value_type")]
    [SuppressGCTransition]
    public static partial int ValueType(nint value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_value_int64")]
    [SuppressGCTransition]
    public static partial long ValueInt64(nint value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_value_double")]
    [SuppressGCTransition]
    public static partial double ValueDouble(nint value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_value_text")]
    public static partial byte* ValueText(nint value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_value_blob")]
    public static partial void* ValueBlob(nint value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_value_bytes")]
    public static partial int ValueBytes(nint value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_key_v2")]
    public static partial int KeyV2(nint db, byte* databaseName, void* key, int keyLength);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_set_authorizer")]
    public static partial int SetAuthorizer(
        nint db,
        delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, byte*, byte*, int> callback,
        nint context);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_create_function_v2")]
    public static partial int CreateFunctionV2(
        nint db,
        byte* name,
        int argumentCount,
        int textRepresentation,
        nint context,
        delegate* unmanaged[Cdecl]<nint, int, nint*, void> function,
        nint step,
        nint final,
        nint destroy);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_user_data")]
    public static partial nint UserData(nint context);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_result_int64")]
    public static partial void ResultInt64(nint context, long value);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_result_error")]
    public static partial void ResultError(nint context, byte* message, int byteCount);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_db_config")]
    public static partial int DbConfigFlag(nint db, int option, int value, int* result);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_backup_init")]
    public static partial nint BackupInit(nint destination, byte* destinationName, nint source, byte* sourceName);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_backup_step")]
    public static partial int BackupStep(nint backup, int pages);

    [LibraryImport(LibraryName, EntryPoint = "sqlite3_backup_finish")]
    public static partial int BackupFinish(nint backup);

    public static string ReadUtf8(byte* pointer) =>
        pointer is null ? string.Empty : Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(pointer));

    public static string? ReadOptionalUtf8(byte* pointer) =>
        pointer is null ? null : Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(pointer));

    public static SqliteStatus ReadValue(nint value, out SqlValue result) => ValueType(value) switch
    {
        TypeInteger => Read(SqlValue.FromIntegral(ValueInt64(value)), out result),
        TypeFloat => Real(ValueDouble(value), out result),
        TypeText => Text(ValueText(value), ValueBytes(value), out result),
        TypeBlob => Read(SqlValue.FromBlob(Blob(ValueBlob(value), ValueBytes(value))), out result),
        _ => Read(SqlValue.Null, out result),
    };

    public static SqliteStatus ReadColumn(nint statement, int index, out SqlValue result) => ColumnType(statement, index) switch
    {
        TypeInteger => Read(SqlValue.FromIntegral(ColumnInt64(statement, index)), out result),
        TypeFloat => Real(ColumnDouble(statement, index), out result),
        TypeText => Text(ColumnText(statement, index), ColumnBytes(statement, index), out result),
        TypeBlob => Read(SqlValue.FromBlob(Blob(ColumnBlob(statement, index), ColumnBytes(statement, index))), out result),
        _ => Read(SqlValue.Null, out result),
    };

    private static SqliteStatus Read(SqlValue value, out SqlValue result)
    {
        result = value;
        return SqliteStatus.Success;
    }

    private static SqliteStatus Real(double value, out SqlValue result)
    {
        if (double.IsFinite(value))
        {
            return Read(SqlValue.FromReal(value), out result);
        }

        result = SqlValue.Null;
        return SqliteStatus.NonFiniteReal;
    }

    private static SqliteStatus Text(byte* text, int length, out SqlValue result)
    {
        if (text is null || length == 0)
        {
            return Read(SqlValue.FromText(string.Empty), out result);
        }

        var bytes = new ReadOnlySpan<byte>(text, length);
        if (Ascii.IsValid(bytes))
        {
            return Read(SqlValue.FromText(Encoding.ASCII.GetString(bytes)), out result);
        }

        if (!Utf8Text.IsValid(bytes))
        {
            result = SqlValue.Null;
            return SqliteStatus.InvalidUtf8;
        }

        return Read(SqlValue.FromText(Encoding.UTF8.GetString(bytes)), out result);
    }

    private static byte[] Blob(void* blob, int length) =>
        blob is null || length == 0 ? [] : new ReadOnlySpan<byte>(blob, length).ToArray();
}

internal static class SqliteLibrary
{
    private const string FileName = "libsqlite3mc.so";

    private static readonly Lock Gate = new();
    private static bool _registered;
    private static bool _heapLimitsApplied;

    public static unsafe string Version
    {
        get
        {
            EnsureInitialized();
            return SqliteNative.ReadUtf8(SqliteNative.LibVersion());
        }
    }

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (!_registered)
            {
                NativeLibrary.SetDllImportResolver(typeof(SqliteLibrary).Assembly, Resolve);
                _registered = true;
            }

            if (!_heapLimitsApplied)
            {
                SqliteLimits.ApplyHeapLimits();
                _heapLimitsApplied = true;
            }
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, SqliteNative.LibraryName, StringComparison.Ordinal))
        {
            return 0;
        }

        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        foreach (var directory in SearchDirectories(architecture))
        {
            var candidate = Path.Combine(directory, FileName);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return NativeLibrary.TryLoad(FileName, assembly, searchPath, out var fallback) ? fallback : 0;
    }

    private static List<string> SearchDirectories(string runtimeIdentifier)
    {
        List<string> directories = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string search)
        {
            foreach (var entry in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(entry);
                Add(Path.Combine(entry, "runtimes", runtimeIdentifier, "native"));
            }
        }

        Add(Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeIdentifier, "native"));
        Add(AppContext.BaseDirectory);
        return directories;

        void Add(string directory)
        {
            if (directory.Length > 0 && seen.Add(directory))
            {
                directories.Add(directory);
            }
        }
    }
}
