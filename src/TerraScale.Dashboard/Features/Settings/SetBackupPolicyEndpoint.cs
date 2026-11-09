using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Settings
{
    public sealed class SetBackupPolicyEndpoint(DashboardSessions sessions, AccountService accounts, SettingsService settings) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/settings/backup-policy");

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
                var retention = int.TryParse(form["retentionDays"], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                await settings.SetBackupPolicyAsync(context, form["databaseId"].ToString(), form["fullBackupCron"].ToString(),
                    form["incrementalBackupCron"].ToString(), retention, http.RequestAborted);
                return HttpRedirects.SeeOther("/");
            }
            catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
            catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Results.Text("The provider could not complete this settings change. Try again shortly.", statusCode: 503); }
        }
    }
}
