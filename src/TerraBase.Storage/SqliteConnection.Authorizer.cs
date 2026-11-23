using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace TerraBase.Storage;

internal sealed class StatementInfo
{
    public bool IsDdl { get; set; }

    public string? Denied { get; set; }

    public string? CreatedTable { get; set; }

    public HashSet<string> WrittenTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ReadTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        IsDdl = false;
        Denied = null;
        CreatedTable = null;
        WrittenTables.Clear();
        ReadTables.Clear();
    }
}

internal sealed unsafe partial class SqliteConnection
{
    private static readonly FrozenSet<string> ReadPragmas = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "table_info",
        "table_xinfo",
        "table_list",
        "index_list",
        "index_info",
        "index_xinfo",
        "foreign_key_list",
        "foreign_key_check",
        "collation_list",
        "function_list",
        "module_list",
        "pragma_list",
        "database_list",
        "compile_options",
        "data_version",
        "freelist_count",
        "page_count",
        "integrity_check",
        "quick_check",
        "encoding");

    private static readonly FrozenSet<string> QueryOnlyPragmas = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "schema_version",
        "user_version",
        "application_id",
        "page_size",
        "foreign_keys",
        "journal_mode");

    private static readonly FrozenSet<string> DeniedTables = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "sqlite_dbpage", "sqlite_dbdata", "sqlite_dbptr", "sqlite_stmt", "sqlite_dbstat", "dbstat", "btree_dump");

    private static readonly FrozenSet<string> DeniedFunctions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "load_extension", "readfile", "writefile");

    private int Authorize(int action, string? first, string? second, string? database)
    {
        if (!_userMode)
        {
            return SqliteNative.AuthOk;
        }

        var info = _collecting;
        var main = database is null or "main";
        switch (action)
        {
            case SqliteNative.ActionTransaction:
                return Deny(info, "transaction control is managed by TerraBase");
            case SqliteNative.ActionAttach or SqliteNative.ActionDetach:
                return Deny(info, "ATTACH and DETACH are not supported");
            case SqliteNative.ActionCreateTempIndex or SqliteNative.ActionCreateTempTable or SqliteNative.ActionCreateTempTrigger or SqliteNative.ActionCreateTempView
                or SqliteNative.ActionDropTempIndex or SqliteNative.ActionDropTempTable or SqliteNative.ActionDropTempTrigger or SqliteNative.ActionDropTempView:
                return Deny(info, "temporary schema objects are not supported");
            case SqliteNative.ActionAnalyze:
                return Deny(info, "ANALYZE is not permitted");
            case SqliteNative.ActionCreateVirtualTable:
                return Deny(info, "virtual tables are not supported");
            case SqliteNative.ActionFunction when second is not null && DeniedFunctions.Contains(second):
                return Deny(info, "the function is not permitted");
            case SqliteNative.ActionSavepoint:
                return IsInternal(second) ? Deny(info, "reserved savepoint name") : SqliteNative.AuthOk;
            case SqliteNative.ActionPragma:
                return first is not null && (ReadPragmas.Contains(first) || (second is null && QueryOnlyPragmas.Contains(first)))
                    ? SqliteNative.AuthOk
                    : Deny(info, string.Concat("PRAGMA ", first, " is not permitted"));
            case SqliteNative.ActionRead:
                if (first is not null && DeniedTables.Contains(first))
                {
                    return Deny(info, "the internal SQLite table is not accessible");
                }

                if (IsInternal(first))
                {
                    return Deny(info, "internal TerraBase tables are not accessible");
                }

                if (main && first is not null && !IsSystem(first))
                {
                    info?.ReadTables.Add(first);
                }

                return SqliteNative.AuthOk;
            case SqliteNative.ActionInsert or SqliteNative.ActionUpdate or SqliteNative.ActionDelete:
                if (IsInternal(first))
                {
                    return Deny(info, "internal TerraBase tables are not writable");
                }

                if (main && first is not null && !IsSystem(first))
                {
                    info?.WrittenTables.Add(first);
                }

                return SqliteNative.AuthOk;
            case SqliteNative.ActionCreateTable or SqliteNative.ActionCreateView
                or SqliteNative.ActionDropTable or SqliteNative.ActionDropView or SqliteNative.ActionDropVirtualTable:
                if (IsInternal(first) || (first is not null && IsSystem(first)))
                {
                    return Deny(info, "reserved table name");
                }

                info?.IsDdl = main;
                if (action == SqliteNative.ActionCreateTable && main)
                {
                    info?.CreatedTable = first;
                }

                return SqliteNative.AuthOk;
            case SqliteNative.ActionCreateIndex or SqliteNative.ActionCreateTrigger or SqliteNative.ActionDropIndex or SqliteNative.ActionDropTrigger:
                if (IsInternal(first) || IsInternal(second))
                {
                    return Deny(info, "reserved object name");
                }

                info?.IsDdl = main;
                return SqliteNative.AuthOk;
            case SqliteNative.ActionAlterTable:
                if (IsInternal(second))
                {
                    return Deny(info, "reserved table name");
                }

                info?.IsDdl = true;
                return SqliteNative.AuthOk;
            default:
                return SqliteNative.AuthOk;
        }
    }

    private static SqliteStatus ValidatePreparedSql(ReadOnlySpan<byte> sql)
    {
        var tokens = SqlTokenizer.Tokenize(Encoding.UTF8.GetString(sql));
        var start = tokens.FindIndex(static token => !token.IsSymbol(";"));
        if (start >= 0 && (tokens[start].Is("VACUUM") || tokens[start].Is("ANALYZE")))
        {
            return new SqliteStatus(SqliteNative.Auth, string.Concat(tokens[start].Text, " is not permitted"));
        }

        return SqliteStatus.Success;
    }

    private static SqliteStatus UserStatus(SqliteStatus status, StatementInfo info)
    {
        if (info.Denied is { } reason)
        {
            return new SqliteStatus(SqliteNative.Auth, reason);
        }

        const string missingFunction = "no such function: ";
        const string missingTable = "no such table: ";
        if (status.Primary != SqliteNative.Error)
        {
            return status;
        }

        if (status.Message.StartsWith(missingFunction, StringComparison.Ordinal) && DeniedFunctions.Contains(status.Message[missingFunction.Length..]))
        {
            return new SqliteStatus(SqliteNative.Auth, "the function is not permitted");
        }

        if (status.Message.StartsWith(missingTable, StringComparison.Ordinal))
        {
            var name = status.Message[missingTable.Length..];
            if (DeniedTables.Contains(name[(name.LastIndexOf('.') + 1)..]))
            {
                return new SqliteStatus(SqliteNative.Auth, "the internal SQLite table is not accessible");
            }
        }

        return status;
    }

    private static int Deny(StatementInfo? info, string reason)
    {
        if (info is not null)
        {
            info.Denied ??= reason;
        }

        return SqliteNative.AuthDeny;
    }

    private static bool IsInternal(string? name) => name is not null && name.StartsWith("_tb_", StringComparison.OrdinalIgnoreCase);

    private static bool IsSystem(string name) => name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnAuthorize(nint context, int action, byte* first, byte* second, byte* database, byte* trigger)
    {
        var connection = GCHandle<SqliteConnection>.FromIntPtr(context).Target;
        return connection.Authorize(action, SqliteNative.ReadOptionalUtf8(first), SqliteNative.ReadOptionalUtf8(second), SqliteNative.ReadOptionalUtf8(database));
    }
}
