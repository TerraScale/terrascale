using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class SetProjectAllowedCidrsEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/credentials/project-allowed-cidrs");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            var cidrs = ApiKeyService.ParseCidrs(form["allowedCidrs"].ToString());
            try
            {
                await keys.SetProjectAllowedCidrsAsync(context, cidrs, http.RequestAborted);
            }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error) || error is HttpRequestException)
            { return DatabaseEndpoints.WantsHtml(http) ? await ApiKeyPages.PageAsync(http, context, keys, databases, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error), draftKeyCidrs: "project", draftAllowedCidrs: form["allowedCidrs"].ToString()) : DatabaseEndpoints.Failure(error); }
            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther("/api-keys" + http.Request.QueryString) : Results.NoContent();
        }
    }
}
