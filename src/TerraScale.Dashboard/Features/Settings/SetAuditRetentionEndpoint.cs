using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Settings
{
    public sealed class SetAuditRetentionEndpoint(DashboardSessions sessions, AccountService accounts, SettingsService settings) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/settings/audit-retention");

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
                return RequestError.FormExpired(http, "/", Results.BadRequest("Invalid form token."));
            }

            try
            {
                _ = int.TryParse(form["retentionDays"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var days);
                await settings.SetAuditRetentionAsync(context, days, http.RequestAborted);
                return HttpRedirects.SeeOther("/");
            }
            catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
            catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Results.Text("The provider could not complete this settings change. Try again shortly.", statusCode: 503); }
        }
    }
}
