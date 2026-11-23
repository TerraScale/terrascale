namespace TerraBase.Features.Sql;

internal static class SqlRoute
{
    public const string Database = "/v1/projects/{project}/databases/{database}";

    public static Result<ExecuteBody, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable> V2(
        Result<SqlRun, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable> outcome) => outcome switch
        {
            SqlRun run => new ExecuteBody(SqlWire.Results(run.Results), run.Bookmark.Encode(), ConsistencyHeaders.Format(run.Mode)),
            SqlRefusal refusal => refusal,
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
}
