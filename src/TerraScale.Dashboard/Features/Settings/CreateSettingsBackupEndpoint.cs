using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Settings
{
    public sealed class CreateSettingsBackupEndpoint(DashboardSessions sessions, AccountService accounts, SettingsService settings) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/settings/backup-now");

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
                await settings.CreateBackupAsync(context, form["databaseId"].ToString(), http.RequestAborted);
                return HttpRedirects.SeeOther("/");
            }
            catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
            catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Results.Text("The provider could not complete this settings change. Try again shortly.", statusCode: 503); }
        }
    }
}
