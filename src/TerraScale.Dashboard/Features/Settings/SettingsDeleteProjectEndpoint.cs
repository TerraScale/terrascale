using Microsoft.Extensions.Primitives;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Settings
{
    public sealed class SettingsDeleteProjectEndpoint(DashboardSessions sessions, AccountService accounts, SettingsService settings) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/settings/delete-project");

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

            try
            {
                await settings.DeleteProjectAsync(context, form["projectId"].ToString(), form["confirmName"].ToString(), http.RequestAborted);
                return HttpRedirects.SeeOther("/organization");
            }
            catch (AccountAccessException error) when (error.Code == "deletion_pending")
            {
                return DeletionProgress.Create(http, new FormCollection(new Dictionary<string, StringValues>
                { ["_csrf"] = form["_csrf"], ["confirmation"] = form["confirmName"] }),
                    "/api/v1/dashboard/organization/projects/" + Uri.EscapeDataString(form["projectId"].ToString()) + "/delete?organizationId=" + Uri.EscapeDataString(context.Organization!.Id));
            }
            catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
            catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Results.Text("The provider could not complete this settings change. Try again shortly.", statusCode: 503); }
        }
    }
}
