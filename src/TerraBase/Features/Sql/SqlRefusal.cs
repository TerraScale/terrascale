using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Http;

namespace TerraBase.Features.Sql;

public sealed record SqlRefusal(
    string Code,
    string Message,
    string SqlState,
    int Statement,
    int Status,
    TimeSpan? RetryAfter = null,
    IReadOnlyDictionary<string, string>? Details = null)
{
    public ApiErrorBody Body => new(Code, Message, SqlState, Statement, Details);

    public static SqlRefusal Invalid(string message, int statement) => new("invalid_request", message, "22023", statement, StatusCodes.Status400BadRequest);

    public static SqlRefusal RequestTooLarge() => new("too_large", "SQL request bodies are at most 1 MiB", "54000", 0, StatusCodes.Status413PayloadTooLarge);

    public static SqlRefusal ForbiddenScope(int statement) =>
        new("forbidden_scope", "writes and schema changes need the sql:write scope", "42501", statement, StatusCodes.Status403Forbidden);

    public static SqlRefusal RuleDenied(string message, int statement, string? operation = null, string? table = null)
    {
        Dictionary<string, string>? details = null;
        if (operation is not null || table is not null)
        {
            details = new(StringComparer.Ordinal);
            if (operation is not null)
            {
                details["operation"] = operation;
            }

            if (table is not null)
            {
                details["table"] = table;
            }
        }

        return new("rule_denied", message, "42501", statement, StatusCodes.Status403Forbidden, Details: details);
    }

    public static SqlRefusal ColumnNotReadable(string column, int statement) =>
        new("column_not_readable", string.Concat(column, " is not readable"), "42501", statement, StatusCodes.Status403Forbidden,
            Details: new Dictionary<string, string>(StringComparer.Ordinal) { ["column"] = column });

    public static SqlRefusal RulesInvalid(string message, int statement) =>
        new("rules_invalid", message, "42501", statement, StatusCodes.Status403Forbidden);

    public static SqlRefusal From(SqlError error) => IsUncertain(error.Code)
        ? Uncertain(error.Code, error.Message, error.StatementIndex)
        : new(MapCode(error.Code, error.SqlState), error.Message, error.SqlState, error.StatementIndex, MapStatus(error.Code));

    public static SqlRefusal From(SerializationConflict conflict) =>
        new("serialization_conflict", string.Concat("the transaction conflicted with a concurrent one and was rolled back (", conflict.Reason, "); retry it"),
            "40001", 0, StatusCodes.Status409Conflict);

    public static SqlRefusal From(SchemaMismatch mismatch) =>
        new("serialization_conflict", "the schema changed while the statement ran; retry it", "40001", 0, StatusCodes.Status409Conflict);

    public static SqlRefusal From(StorageBusy busy) =>
        new("storage_busy", busy.Message, "55P03", 0, StatusCodes.Status503ServiceUnavailable, busy.RetryAfter > TimeSpan.Zero ? busy.RetryAfter : TimeSpan.FromSeconds(1));

    public static SqlRefusal From(StorageTimeout timeout) => new("request_timeout", timeout.Message, "57014", 0, StatusCodes.Status504GatewayTimeout);

    public static SqlRefusal From(ClusterTimeout timeout) => new("request_timeout", timeout.Message, "57014", 0, StatusCodes.Status504GatewayTimeout);

    public static SqlRefusal From(ClusterUnavailable unavailable) =>
        Unavailable(unavailable.Code, unavailable.Message);

    public static SqlRefusal From(BackendUnavailable unavailable) =>
        Unavailable(unavailable.Code, unavailable.Message);

    private static SqlRefusal Unavailable(string code, string message) => IsUncertain(code)
        ? Uncertain(code, message, 0)
        : new(code, message, "57P03", 0, StatusCodes.Status503ServiceUnavailable, TimeSpan.FromSeconds(1));

    private static bool IsUncertain(string code) => code is "in_region_quorum_unconfirmed" or "write_outcome_unknown" or "commit_uncertain";

    private static SqlRefusal Uncertain(string code, string message, int statement) =>
        new(code, message, "08007", statement, StatusCodes.Status503ServiceUnavailable);

    private static string MapCode(string code, string sqlState) => (code, sqlState) switch
    {
        ("sql_error", "42601") => "syntax_error",
        ("sql_error", "42P01") => "undefined_table",
        ("sql_error", "42703") => "undefined_column",
        ("sql_error", "42P07") => "duplicate_object",
        ("sql_error", _) => "sql_error",
        ("not_authorized", _) => "sql_sandbox",
        ("strong_required", _) => "serialization_conflict",
        _ => code,
    };

    private static int MapStatus(string code) => code switch
    {
        "not_authorized" => StatusCodes.Status403Forbidden,
        "storage_busy" => StatusCodes.Status503ServiceUnavailable,
        "strong_required" or "serialization_conflict" => StatusCodes.Status409Conflict,
        "storage_corrupted" or "io_error" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };
}
