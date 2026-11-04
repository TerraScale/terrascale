namespace TerraBase.Features.Sql;

internal static class SqlErrors
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
    public const string SerializationConflict = "serialization_conflict";
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
    public const string WrongRegion = "wrong_region";
    public const string InternalError = "internal_error";

    public static string Sqlstate(string code) => code switch
    {
        SyntaxError => "42601",
        UndefinedTable => "42P01",
        UndefinedColumn => "42703",
        DuplicateObject => "42P07",
        PrimaryKeyViolation or UniqueViolation => "23505",
        NotNullViolation => "23502",
        CheckViolation => "23514",
        ForeignKeyViolation => "23503",
        ConstraintViolation => "23000",
        ForbiddenScope => "42501",
        SerializationConflict => "40001",
        TooLarge => "54000",
        QueryCanceled => "57014",
        TransactionRolledBack => "40000",
        TransactionAborted => "25P02",
        ReadOnlyTransaction => "25006",
        QuotaExceeded => "53100",
        InvalidRequest => "22023",
        TransactionState => "25000",
        TenantBusy => "53300",
        Unavailable => "57P03",
        Unauthorized => "28000",
        WrongRegion => "08004",
        InternalError => "XX000",
        _ => "0A000",
    };

    public static int Status(string code) => code switch
    {
        Unauthorized => StatusCodes.Status401Unauthorized,
        ForbiddenScope => StatusCodes.Status403Forbidden,
        SerializationConflict => StatusCodes.Status409Conflict,
        TooLarge => StatusCodes.Status413PayloadTooLarge,
        QuotaExceeded or TenantBusy => StatusCodes.Status429TooManyRequests,
        Unavailable => StatusCodes.Status503ServiceUnavailable,
        WrongRegion => StatusCodes.Status421MisdirectedRequest,
        InternalError => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status400BadRequest,
    };

    public static SqlError Error(string message) => new(message switch
    {
        _ when message.StartsWith("no such table", StringComparison.Ordinal) => UndefinedTable,
        _ when message.StartsWith("no such column", StringComparison.Ordinal) => UndefinedColumn,
        _ when message.StartsWith("already exists", StringComparison.Ordinal) => DuplicateObject,
        _ when message.StartsWith("SQL syntax error", StringComparison.Ordinal) => SyntaxError,
        _ => SqlError,
    }, message);

    public static SqlError NonFinite { get; } = Error("nonfinite numbers are not supported");

    public static SqlError Auth { get; } = new(ForbiddenScope, "writes and schema changes need the documents:write scope");

    public static SqlError Busy(string message) => new(SerializationConflict, message);

    public static SqlError TooBig(string message) => new(TooLarge, message);

    public static SqlError Quota(string message) => new(QuotaExceeded, message);

    public static SqlError Interrupt { get; } = new(QueryCanceled, "query too expensive: it exceeds the work budget");

    public static SqlError InFailedTransaction { get; } =
        new(TransactionAborted, "current transaction is aborted, commands ignored until end of transaction block");
}
