using TerraBase.Hosting;

namespace TerraBase.Features.Sql;

internal readonly union ExecuteSqlResponse(ExecuteBody, SqlRefusal, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal static class SqlRoute
{
    public const string Database = "/v1/projects/{project}/databases/{database}";

    public static ExecuteSqlResponse V2(
        SqlRunResolution outcome) => outcome switch
        {
            SqlRun run => new ExecuteBody([], SqlRunner.EncodeBookmark(run.Bookmark), ConsistencyHeaders.Format(run.Mode)) { RawResults = run.Results },
            SqlRefusal refusal => refusal,
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
}
