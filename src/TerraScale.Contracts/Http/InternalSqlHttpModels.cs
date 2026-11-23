using System.Text.Json;

namespace TerraScale.Contracts.Http;

public sealed record SqlHttpStatement(string Sql, IReadOnlyList<JsonElement>? Args, IReadOnlyDictionary<string, JsonElement>? Named);

public sealed record SqlExecuteRequest(IReadOnlyList<SqlHttpStatement> Statements);

public sealed record SqlHttpColumn(string Name, string? Type);

public sealed record SqlHttpStatementResult(
    IReadOnlyList<SqlHttpColumn> Columns,
    IReadOnlyList<IReadOnlyList<JsonElement>> Rows,
    long RowsAffected,
    long LastInsertRowId);

public sealed record SqlExecuteResponse(IReadOnlyList<SqlHttpStatementResult> Results, string Bookmark, string Consistency);

public sealed record SqlWsClientFrame(
    string Type,
    long Id,
    IReadOnlyList<SqlHttpStatement>? Statements,
    string? Consistency,
    string? Bookmark);

public sealed record SqlWsServerFrame(
    string Type,
    long Id,
    IReadOnlyList<SqlHttpStatementResult>? Results,
    string? Bookmark,
    bool Transaction,
    ApiErrorBody? Error);

public sealed record ApiErrorBody(
    string Code,
    string Message,
    string? SqlState,
    int? Statement,
    IReadOnlyDictionary<string, string>? Details);

public sealed record ApiErrorEnvelope(ApiErrorBody Error);

public static class SqlWsFrameTypes
{
    public const string Subprotocol = "terrascale.sql.v2";
    public const string Execute = "execute";
    public const string Batch = "batch";
    public const string Begin = "begin";
    public const string Commit = "commit";
    public const string Rollback = "rollback";
    public const string Result = "result";
    public const string Error = "error";
}
