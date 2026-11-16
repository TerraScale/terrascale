using System.Text.RegularExpressions;
using TerraBase.Engine;

namespace TerraBase.Features.Sql;

internal static partial class SqlErrors
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
    public const string Sandbox = "sql_sandbox";
    public const string SerializationConflict = "serialization_conflict";
    public const string RowConflict = "row_conflict";
    public const string TooLarge = "too_large";
    public const string QueryCanceled = "query_canceled";
    public const string WriteOutcomeUnknown = "write_outcome_unknown";
    public static SqlError OutcomeUnknown(string home) => new(WriteOutcomeUnknown,
        $"the commit was sent to the database's home region {home} and its answer was lost, so it may have committed; the session is closed; reconcile by reading back what it would have written before writing again");
    public static readonly SqlError TransactionExpired = new(QueryCanceled, "the transaction expired: it was open for more than 5 seconds and was rolled back");
    public const string TransactionRolledBack = "transaction_rolled_back";
    public const string TransactionAborted = "transaction_aborted";
    public const string ReadOnlyTransaction = "read_only_transaction";
    public const string QuotaExceeded = "quota_exceeded";
    public const string InvalidRequest = "invalid_request";
    public const string TransactionState = "transaction_state";
    public const string TenantBusy = "tenant_busy";
    public const string Unavailable = "unavailable";
    public const string CopyFaulted = "copy_faulted";
    public const string RecoveryPending = "recovery_pending";
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
        ForbiddenScope or Sandbox => "42501",
        SerializationConflict => "40001",
        RowConflict => "P0001",
        TooLarge => "54000",
        QueryCanceled => "57014",
        WriteOutcomeUnknown => "08007",
        TransactionRolledBack => "40000",
        TransactionAborted => "25P02",
        ReadOnlyTransaction => "25006",
        QuotaExceeded => "53100",
        InvalidRequest => "22023",
        TransactionState => "25000",
        TenantBusy => "53300",
        Unavailable or CopyFaulted or RecoveryPending => "57P03",
        Unauthorized => "28000",
        WrongRegion => "08004",
        InternalError => "XX000",
        _ => "0A000",
    };

    public static int Status(string code) => code switch
    {
        Unauthorized => StatusCodes.Status401Unauthorized,
        ForbiddenScope or Sandbox => StatusCodes.Status403Forbidden,
        SerializationConflict or RowConflict => StatusCodes.Status409Conflict,
        TooLarge => StatusCodes.Status413PayloadTooLarge,
        QuotaExceeded or TenantBusy => StatusCodes.Status429TooManyRequests,
        Unavailable or CopyFaulted or RecoveryPending or WriteOutcomeUnknown => StatusCodes.Status503ServiceUnavailable,
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

    public const string StaleRowMessage = "terrascale: the statement changed a different number of rows than expected";

    public static (SqlFailure Failure, string Message) Classify(string message, bool parse, bool interrupted, int sqliteCode = 0)
    {
        var primary = sqliteCode & 0xff;
        if (primary is 11 or 26)
        {
            return (new StorageFailure(), "the database reported corruption");
        }
        if (primary is SqliteBusy or SqliteLocked or SqliteNoMem or SqliteIoErr or SqliteFull or SqliteCantOpen)
        {
            return (new Busy(), "the database is busy; retry");
        }

        return message switch
        {
            _ when message.Contains(StaleRowMessage, StringComparison.Ordinal) => (new StaleRow(), "the row was changed or deleted since it was read"),
            _ when Has(message, "UNIQUE constraint failed") => (new Unique(), "UNIQUE constraint failed" + Names(message)),
            _ when Has(message, "NOT NULL constraint failed") => (new NotNull(), "NOT NULL constraint failed" + Names(message)),
            _ when Has(message, "CHECK constraint failed") => (new Check(), "CHECK constraint failed" + Names(message)),
            _ when Has(message, "FOREIGN KEY constraint failed") => (new ForeignKeyFailure(), "FOREIGN KEY constraint failed"),
            _ when Has(message, "constraint failed") || primary == SqliteConstraint => (new Constraint(), "constraint failed"),
            _ when Has(message, "no such table") => (new NoSuchTable(), "no such table" + Names(message)),
            _ when Has(message, "no such column") => (new NoSuchColumn(), "no such column" + Names(message)),
            _ when Has(message, "already exists") => (new AlreadyExists(), "already exists" + Existing(message)),
            _ when primary == SqliteInterrupt || interrupted || Has(message, "interrupt") => (new Interrupted(), "the statement was interrupted"),
            _ when Has(message, "readonly") || Has(message, "read-only") || Has(message, "query_only") => (new ReadOnly(), "attempt to write a readonly database"),
            _ when primary == SqliteTooBig || Has(message, "too big") || Has(message, "too large") || Has(message, "too many") => (new TooBig(), "a value or statement is too big"),
            _ when Has(message, "syntax error") || Has(message, "near \"") || Has(message, "unexpected") || Has(message, "unrecognized token") =>
                (new Syntax(), "SQL syntax error"),
            _ when SqlPhrases.FirstOrDefault(phrase => Has(message, phrase)) is { } phrase => (new KnownError(), phrase),
            _ when Has(message, "database is locked") || Has(message, "database is busy") || Has(message, "schema changed") || Has(message, "schema conflict")
                || Has(message, "write-write conflict") => (new Busy(), "the database is busy; retry"),
            _ when parse => (new Syntax(), "SQL syntax error"),
            _ => (new Error(), message),
        };
    }

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteNoMem = 7;
    private const int SqliteInterrupt = 9;
    private const int SqliteIoErr = 10;
    private const int SqliteFull = 13;
    private const int SqliteCantOpen = 14;
    private const int SqliteTooBig = 18;
    private const int SqliteConstraint = 19;

    public static bool IsLocked(string message) => message.Contains("database is locked", StringComparison.Ordinal);

    public static bool IsConstraintFailure(string message) => message.Contains("constraint failed", StringComparison.OrdinalIgnoreCase);

    public static bool IsForeignKeyFailure(string message) => message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] SqlPhrases =
    [
        "no such function", "ambiguous column", "misuse of aggregate", "misuse of window function", "circular reference",
        "wrong number of arguments", "datatype mismatch", "integer overflow", "not supported", "not allowed", "cannot",
        "malformed JSON", "must be",
    ];

    private static bool Has(string message, string phrase) => message.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    internal static string Names(string message)
    {
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
        {
            return "";
        }

        var names = message[(colon + 2)..];
        var end = names.IndexOf(" (", StringComparison.Ordinal);
        names = (end < 0 ? names : names[..end]).Trim();
        return names.Length is > 0 and <= 512 && NamePattern().IsMatch(names) ? ": " + names : "";
    }

    public static List<(string Table, string Name)> ConstraintColumns(string message)
    {
        var names = Names(message);
        return names.Length == 0 ? [] : [.. names[2..].Split(", ").Select(name => name.Split('.')).Where(parts => parts.Length == 2).Select(parts => (parts[0], parts[1]))];
    }

    private static string Existing(string message) =>
        ExistingPattern().Match(message) is { Success: true } match && NamePattern().IsMatch(match.Groups[2].Value)
            ? $": {match.Groups[1].Value.ToLowerInvariant()} {match.Groups[2].Value}"
            : "";

    [GeneratedRegex(@"\A[\p{L}\p{N}_$.]+(?:, [\p{L}\p{N}_$.]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\b(table|index|view|trigger)\s+[""'`\[]?([^\s""'`\]]+)[""'`\]]?\s+already exists", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExistingPattern();

    public static SqlError NonFinite { get; } = Error("nonfinite numbers are not supported");

    public static SqlError Auth { get; } = new(ForbiddenScope, "writes and schema changes need the sql:write scope");

    public static SqlError Busy(string message) => new(SerializationConflict, message);

    public static SqlError Moved(Engine.DatabaseMovedException moved) => new(WrongRegion, moved.Message);

    public static SqlError Fenced(DatabaseFencedException fenced) => new(Unavailable, fenced.Message);

    public static SqlError TooBig(string message) => new(TooLarge, message);

    public static SqlError Quota(string message) => new(QuotaExceeded, message);

    public static SqlError Interrupt { get; } = new(QueryCanceled, "query too expensive: it exceeds the work budget");

    public static SqlError Deadline { get; } = new(QueryCanceled,
        $"the request exceeded its {(int)SqlLimits.MaxRequestTime.TotalSeconds} s time limit; split the batch or narrow the query");

    public static SqlError InFailedTransaction { get; } =
        new(TransactionAborted, "current transaction is aborted, commands ignored until end of transaction block");
}
