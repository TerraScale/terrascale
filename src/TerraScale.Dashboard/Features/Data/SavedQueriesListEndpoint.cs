using TerraScale.Dashboard.Features.Databases;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed class SavedQueriesListEndpoint(DashboardSessions sessions, AccountService accounts, SavedQueryService saved) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/api/v1/dashboard/data/saved-queries");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var (limit, after) = CursorPaging.Read(http.Request);
                return Results.Json(CursorPaging.Page((await saved.ListAsync(context, http.RequestAborted)).Select(DataEndpoints.PublicSavedQuery),
                    query => query.Id, CursorPaging.Scope(context, "saved-queries"), limit, after));
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error)) { return DatabaseResults.Failure(error); }
        }
    }
}
