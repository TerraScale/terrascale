using TerraScale.Dashboard.Features.Databases;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed class SavedQueriesListEndpoint(DashboardSessions sessions, AccountService accounts, SavedQueryService saved) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/api/v1/dashboard/data/saved-queries").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (!AccountRequest.TryResolveApi(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            try
            {
                var (limit, after) = CursorPaging.Read(http.Request);
                return TypedResults.Json(CursorPaging.Page((await saved.ListAsync(context, http.RequestAborted)).Select(DataEndpoints.PublicSavedQuery),
                    query => query.Id, CursorPaging.Scope(context, "saved-queries"), limit, after));
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error)) { return DatabaseResults.Failure(error); }
        }
    }
}
