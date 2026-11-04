using System.Net;

namespace TerraScale.DotNet.TerraBase;

/// <summary>TerraScale SQL error codes, the same on REST, the WebSocket and the PostgreSQL wire.</summary>
public static class TerraBaseSqlErrorCodes
{
    /// <summary>A refusal without a more specific code (SQLSTATE 0A000).</summary>
    public const string SqlError = "sql_error";

    /// <summary>The statement does not parse (42601).</summary>
    public const string SyntaxError = "syntax_error";

    /// <summary>No such table (42P01).</summary>
    public const string UndefinedTable = "undefined_table";

    /// <summary>No such column (42703).</summary>
    public const string UndefinedColumn = "undefined_column";

    /// <summary>The object already exists (42P07).</summary>
    public const string DuplicateObject = "duplicate_object";

    /// <summary>A primary key already exists (23505).</summary>
    public const string PrimaryKeyViolation = "primary_key_violation";

    /// <summary>A unique key already exists (23505).</summary>
    public const string UniqueViolation = "unique_violation";

    /// <summary>A NOT NULL column got NULL (23502).</summary>
    public const string NotNullViolation = "not_null_violation";

    /// <summary>A CHECK constraint failed (23514).</summary>
    public const string CheckViolation = "check_violation";

    /// <summary>A foreign key failed (23503).</summary>
    public const string ForeignKeyViolation = "foreign_key_violation";

    /// <summary>Another constraint failed (23000).</summary>
    public const string ConstraintViolation = "constraint_violation";

    /// <summary>Writes and schema changes need the sql:write scope (42501, HTTP 403).</summary>
    public const string ForbiddenScope = "forbidden_scope";

    /// <summary>The statement leaves the database's SQL sandbox, such as ATTACH, VACUUM or a file function (42501, HTTP 403).</summary>
    public const string SqlSandbox = "sql_sandbox";

    /// <summary>A concurrent writer won; the request rolled back and may be sent again (40001, HTTP 409).</summary>
    public const string SerializationConflict = "serialization_conflict";

    /// <summary>A statement, result or transaction passed a size limit (54000, HTTP 413).</summary>
    public const string TooLarge = "too_large";

    /// <summary>The statement ran out of time or work budget (57014).</summary>
    public const string QueryCanceled = "query_canceled";

    /// <summary>The transaction rolled back (40000).</summary>
    public const string TransactionRolledBack = "transaction_rolled_back";

    /// <summary>A statement failed inside the transaction; only rollback or commit is accepted (25P02).</summary>
    public const string TransactionAborted = "transaction_aborted";

    /// <summary>The transaction is read-only (25006).</summary>
    public const string ReadOnlyTransaction = "read_only_transaction";

    /// <summary>The organization's storage quota is used up (53100, HTTP 429).</summary>
    public const string QuotaExceeded = "quota_exceeded";

    /// <summary>The request is malformed (22023).</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>Transaction control is not valid here (25000).</summary>
    public const string TransactionState = "transaction_state";

    /// <summary>The organization's request budget is used up; retry later (53300, HTTP 429).</summary>
    public const string TenantBusy = "tenant_busy";

    /// <summary>The database is not available right now (57P03, HTTP 503).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The credential is missing, revoked or lost its scope (28000, HTTP 401).</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>An unexpected server failure (XX000, HTTP 500).</summary>
    public const string InternalError = "internal_error";
}

/// <summary>
/// A refused SQL statement: <c>{"code","message","statement","sqlstate"}</c> from REST or a WebSocket error frame. The
/// failing request rolled back. <see cref="Code"/> is one of <see cref="TerraBaseSqlErrorCodes"/>. Over the WebSocket
/// <see cref="HttpRequestException.StatusCode"/> is null.
/// </summary>
public sealed class TerraBaseSqlException : TerraBaseException
{
    internal TerraBaseSqlException(HttpStatusCode? statusCode, string message, string code, int statement, string sqlstate)
        : base(statusCode, message, code, statement)
    {
        Code = code;
        Statement = statement;
        Sqlstate = sqlstate;
    }

    /// <summary>The TerraScale error code, the same as <see cref="TerraBaseException.ErrorCode"/>.</summary>
    public string Code { get; }

    /// <summary>Zero-based index of the failing statement.</summary>
    public int Statement { get; }

    /// <summary>The PostgreSQL SQLSTATE of <see cref="Code"/>, such as <c>23505</c>.</summary>
    public string Sqlstate { get; }

    /// <summary>Whether sending the same request again can succeed: <see cref="TerraBaseSqlErrorCodes.SerializationConflict"/> or <see cref="TerraBaseSqlErrorCodes.TenantBusy"/>.</summary>
    public bool IsTransient => Code is TerraBaseSqlErrorCodes.SerializationConflict or TerraBaseSqlErrorCodes.TenantBusy;
}
