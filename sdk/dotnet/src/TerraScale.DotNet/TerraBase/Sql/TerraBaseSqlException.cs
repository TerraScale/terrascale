using System.Net;

namespace TerraScale.DotNet.TerraBase;

public static class TerraBaseSqlErrorCodes
{
    public const string SqlError = "sql_error";

    public const string SyntaxError = "syntax_error";

    public const string UndefinedTable = "undefined_table";

    public const string UndefinedColumn = "undefined_column";

    public const string DuplicateObject = "duplicate_object";

    public const string PrimaryKeyViolation = "primary_key_violation";

    public const string UniqueViolation = "unique_violation";

    public const string NotNullViolation = "not_null_violation";

    public const string CheckViolation = "check_violation";

    public const string ForeignKeyViolation = "foreign_key_violation";

    public const string ConstraintViolation = "constraint_violation";

    public const string ForbiddenScope = "forbidden_scope";

    public const string SqlSandbox = "sql_sandbox";

    public const string SerializationConflict = "serialization_conflict";

    public const string SchemaMismatch = "schema_mismatch";

    public const string InvalidBookmark = "invalid_bookmark";

    public const string InvalidConsistency = "invalid_consistency";

    public const string RowConflict = "row_conflict";

    public const string TooLarge = "too_large";

    public const string QueryCanceled = "query_canceled";

    public const string TransactionRolledBack = "transaction_rolled_back";

    public const string TransactionAborted = "transaction_aborted";

    public const string ReadOnlyTransaction = "read_only_transaction";

    public const string QuotaExceeded = "quota_exceeded";

    public const string InvalidRequest = "invalid_request";

    public const string TransactionState = "transaction_state";

    public const string TenantBusy = "tenant_busy";

    public const string Unavailable = "unavailable";

    public const string Unauthorized = "unauthorized";

    public const string InternalError = "internal_error";

    internal static string? ConsistencySqlState(string code) => code switch
    {
        SerializationConflict or SchemaMismatch => "40001",
        InvalidBookmark or InvalidConsistency => "22023",
        _ => null,
    };
}

public sealed class TerraBaseSqlException : TerraBaseException
{
    internal TerraBaseSqlException(HttpStatusCode? statusCode, string message, string code, int statement, string sqlstate, Exception? inner = null)
        : base(statusCode, message, code, statement, inner)
    {
        Code = code;
        Statement = statement;
        Sqlstate = sqlstate;
    }

    public string Code { get; }

    public int Statement { get; }

    public string Sqlstate { get; }

    public bool IsTransient => Code is TerraBaseSqlErrorCodes.SerializationConflict or TerraBaseSqlErrorCodes.SchemaMismatch or TerraBaseSqlErrorCodes.TenantBusy;
}
