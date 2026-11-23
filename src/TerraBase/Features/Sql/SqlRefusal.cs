using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Http;

namespace TerraBase.Features.Sql;

public sealed record SqlRefusal(string Code, string Message, string SqlState, int Statement, int Status, TimeSpan? RetryAfter = null)
{
    public ApiErrorBody Body => new(Code, Message, SqlState, Statement, null);

    public static SqlRefusal Invalid(string message, int statement) => new("invalid_request", message, "22023", statement, StatusCodes.Status400BadRequest);

    public static SqlRefusal ForbiddenScope(int statement) =>
        new("forbidden_scope", "writes and schema changes need the sql:write scope", "42501", statement, StatusCodes.Status403Forbidden);

    public static SqlRefusal RuleDenied(string message, int statement) => new("rule_denied", message, "42501", statement, StatusCodes.Status403Forbidden);

    public static SqlRefusal From(SqlError error) => new(MapCode(error.Code, error.SqlState), error.Message, error.SqlState, error.StatementIndex, MapStatus(error.Code));

    public static SqlRefusal From(SerializationConflict conflict) =>
        new("serialization_conflict", string.Concat("the transaction conflicted with a concurrent one and was rolled back (", conflict.Reason, "); retry it"),
            "40001", 0, StatusCodes.Status409Conflict);

    public static SqlRefusal From(SchemaMismatch mismatch) =>
        new("serialization_conflict", "the schema changed while the statement ran; retry it", "40001", 0, StatusCodes.Status409Conflict);

    public static SqlRefusal From(StorageBusy busy) =>
        new("storage_busy", busy.Message, "55P03", 0, StatusCodes.Status503ServiceUnavailable, busy.RetryAfter > TimeSpan.Zero ? busy.RetryAfter : TimeSpan.FromSeconds(1));

    public static SqlRefusal From(RequestTimeout timeout) => new("request_timeout", timeout.Message, "57014", 0, StatusCodes.Status504GatewayTimeout);

    public static SqlRefusal From(Unavailable unavailable) =>
        new(unavailable.Code, unavailable.Message, "57P03", 0, StatusCodes.Status503ServiceUnavailable, TimeSpan.FromSeconds(1));

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
        "strong_required" => StatusCodes.Status409Conflict,
        "storage_corrupted" or "io_error" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };
}
