using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace TerraBase.Storage;

internal enum SavepointOperation
{
    Begin,
    Release,
    Rollback,
}

internal sealed record UserSavepoint(SavepointOperation Operation, string Name);

internal sealed class StatementInfo
{
    public bool IsDdl { get; set; }

    public bool IsUncacheable { get; set; }

    public UserSavepoint? Savepoint { get; set; }

    public string? Denied { get; set; }

    public string? CreatedTable { get; set; }

    public SqlRuleScope? Scope { get; set; }

    public IReadOnlySet<string> Views { get; set; } = FrozenSet<string>.Empty;

    public HashSet<string> WrittenTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ReadTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        IsDdl = false;
        IsUncacheable = false;
        Savepoint = null;
        Denied = null;
        CreatedTable = null;
        Scope = null;
        Views = FrozenSet<string>.Empty;
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

    private static readonly FrozenSet<string> ScopedTableFunctions = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance, "json_each", "json_tree");

    private int Authorize(int action, string? first, string? second, string? database, string? inner)
    {
        if (!_userMode)
        {
            return SqliteNative.AuthOk;
        }

        var info = _collecting;
        var main = database is null or "main";
        if (info?.Scope is { } scope && Scoped(info, scope, action, first, second, database, inner) is { } scoped)
        {
            return scoped;
        }

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
                info?.IsUncacheable = true;
                return Savepoint(info, first, second);
            case SqliteNative.ActionPragma:
                info?.IsUncacheable = true;
                return first is not null && !IsInternal(second) && (ReadPragmas.Contains(first) || (second is null && QueryOnlyPragmas.Contains(first)))
                    ? SqliteNative.AuthOk
                    : Deny(info, string.Concat("PRAGMA ", first, " is not permitted"));
            case SqliteNative.ActionReindex:
                info?.IsUncacheable = true;
                return SqliteNative.AuthOk;
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

                info?.IsUncacheable = true;
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

                info?.IsUncacheable = true;
                info?.IsDdl = main;
                return SqliteNative.AuthOk;
            case SqliteNative.ActionAlterTable:
                if (IsInternal(second))
                {
                    return Deny(info, "reserved table name");
                }

                info?.IsUncacheable = true;
                info?.IsDdl = true;
                return SqliteNative.AuthOk;
            default:
                return SqliteNative.AuthOk;
        }
    }

    private static int? Scoped(StatementInfo info, SqlRuleScope scope, int action, string? first, string? second, string? database, string? inner)
    {
        var direct = inner is null || scope.Ctes.Contains(inner);
        if (action == SqliteNative.ActionPragma)
        {
            return Deny(info, "end users cannot run PRAGMA statements");
        }

        if (action == SqliteNative.ActionUpdate)
        {
            return direct && scope.Target is { WritableColumns: { } writable } written && AsciiIgnoreCaseComparer.Instance.Equals(written.Table, first)
                && second is { } column && !writable.Contains(column)
                ? Deny(info, string.Concat(column, " is not a writable column of ", first))
                : null;
        }

        if (action != SqliteNative.ActionRead || first is null || DeniedTables.Contains(first))
        {
            return null;
        }

        if (database is null && (scope.Ctes.Contains(first) || scope.Guards.ContainsKey(first)))
        {
            return SqliteNative.AuthOk;
        }

        if (IsInternal(first) || ScopedTableFunctions.Contains(first))
        {
            return null;
        }

        if (database is not (null or "main"))
        {
            return Deny(info, "end users read only tables of the main database");
        }

        if (inner is not null && scope.Guards.TryGetValue(inner, out var guarded))
        {
            if (!AsciiIgnoreCaseComparer.Instance.Equals(guarded, first))
            {
                return Deny(info, string.Concat("the read rule of ", guarded, " cannot read ", first));
            }

            return scope.Tables.TryGetValue(first, out var table) && Hidden(table.VisibleColumns, second) ? SqliteNative.AuthIgnore : null;
        }

        if (inner is not null && !direct && info.Views.Contains(inner))
        {
            return null;
        }

        if (scope.Target is { } target && AsciiIgnoreCaseComparer.Instance.Equals(target.Table, first))
        {
            return Hidden(direct ? target.VisibleColumns : target.ReadableColumns, second)
                ? Deny(info, string.Concat(second, " of ", first, " is not readable"))
                : null;
        }

        if (direct && second is { Length: 0 } && scope.Tables.ContainsKey(first))
        {
            return null;
        }

        return Deny(info, string.Concat("the rules do not allow reading ", first, " outside its read rule"));
    }

    private static bool Hidden(IReadOnlySet<string>? visible, string? column) =>
        visible is not null && column is { Length: > 0 } name && !visible.Contains(name);

    private static int Savepoint(StatementInfo? info, string? operation, string? name)
    {
        if (IsInternal(name))
        {
            return Deny(info, "reserved savepoint name");
        }

        SavepointOperation? parsed = operation switch
        {
            "BEGIN" => SavepointOperation.Begin,
            "RELEASE" => SavepointOperation.Release,
            "ROLLBACK" => SavepointOperation.Rollback,
            _ => null,
        };
        if (parsed is not { } kind || name is null)
        {
            return Deny(info, "unsupported savepoint statement");
        }

        info?.Savepoint = new UserSavepoint(kind, name);
        return SqliteNative.AuthOk;
    }

    private static SqliteStatus ValidatePreparedSql(ReadOnlySpan<byte> sql)
    {
        var tokens = SqlTokenizer.Tokenize(Encoding.UTF8.GetString(sql));
        var start = tokens.FindIndex(static token => !token.IsSymbol(";"));
        if (start >= 0 && (tokens[start].Is("VACUUM") || tokens[start].Is("ANALYZE")))
        {
            return new SqliteStatus(SqliteNative.Auth, string.Concat(tokens[start].Text, " is not permitted"));
        }

        if (start >= 0 && tokens[start].Is("ALTER") && AlterNamesInternal(tokens, start))
        {
            return new SqliteStatus(SqliteNative.Auth, "reserved table name");
        }

        for (var index = 0; index + 2 < tokens.Count; index++)
        {
            if (tokens[index].Kind == TokenKind.Word && tokens[index].Text.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase)
                && tokens[index + 1].IsSymbol("(") && IsInternal(Unquoted(tokens[index + 2])))
            {
                return new SqliteStatus(SqliteNative.Auth, "internal TerraBase tables are not accessible");
            }
        }

        return SqliteStatus.Success;
    }

    private static bool AlterNamesInternal(List<SqlToken> tokens, int start)
    {
        for (var index = start; index < tokens.Count; index++)
        {
            if (tokens[index].IsIdentifier && IsInternal(tokens[index].Text))
            {
                return true;
            }

            if (tokens[index].Is("RENAME") && index + 1 < tokens.Count && tokens[index + 1].Is("TO")
                && (index + 2 >= tokens.Count || !(tokens[index + 2].IsIdentifier || tokens[index + 2].Kind == TokenKind.Literal) || IsInternal(Unquoted(tokens[index + 2]))))
            {
                return true;
            }
        }

        return false;
    }

    private static string Unquoted(SqlToken token) =>
        token.Kind == TokenKind.Literal && token.Text is ['\'', .., '\'']
            ? token.Text[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : token.Text;

    private static SqliteStatus UserStatus(SqliteStatus status, StatementInfo info)
    {
        if (info.Denied is { } reason)
        {
            return new SqliteStatus(SqliteNative.Auth, reason);
        }

        const string missingFunction = "no such function: ";
        const string missingTable = "no such table: ";
        status = ResourceStatus(status);
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

    private static SqliteStatus ResourceStatus(SqliteStatus status) => status.Primary switch
    {
        SqliteNative.TooBig when status.Message == "string or blob too big" => status with { Message = "a value is at most 16 MiB" },
        SqliteNative.NoMem => status with { Message = "the server ran short of memory for the statement; retry it, or simplify it if it keeps failing" },
        _ => status,
    };

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
        try
        {
            var connection = GCHandle<SqliteConnection>.FromIntPtr(context).Target;
            if (!connection._userMode)
            {
                return SqliteNative.AuthOk;
            }

            return connection.Authorize(action, SqliteNative.ReadOptionalUtf8(first), SqliteNative.ReadOptionalUtf8(second), SqliteNative.ReadOptionalUtf8(database),
                SqliteNative.ReadOptionalUtf8(trigger));
        }
        catch (Exception error)
        {
            try
            {
                var connection = GCHandle<SqliteConnection>.FromIntPtr(context).Target;
                _ = Deny(connection._collecting, error.Message);
            }
            catch
            {
            }

            return SqliteNative.AuthDeny;
        }
    }
}
