using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Endpoints;
using System.Diagnostics;

namespace TerraScale.Dashboard.Features.Usage
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;
    public sealed class SpendingCapEndpoint(DashboardSessions sessions, AccountService accounts, SpendingService spending, UsageService usage) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/usage/spending-cap");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            IFormCollection form;
            switch (await FormRequests.ReadAsync(http, http.RequestAborted))
            {
                case Validation invalid:
                    return FormRequests.Refused(http, invalid);
                case UnsupportedMediaType unsupported:
                    return FormRequests.Refused(http, unsupported);
                case IFormCollection parsed:
                    form = parsed;
                    break;
                case null:
                    throw new UnreachableException();
            }
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/usage?tab=settings", Results.BadRequest("Invalid form token."));
            }

            if (!context.CanManage)
            {
                return Results.Text("Organization management is required.", statusCode: 403);
            }

            if (!SpendingForm.TryParse(form, out var capCents))
            {
                return await SavedAsync(http, sessions, accounts, usage, "invalid");
            }

            try { await spending.SetCapAsync(context, capCents, http.RequestAborted); }
            catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
            return await SavedAsync(http, sessions, accounts, usage, "ok");
        }

        private static async Task<IResult> SavedAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, UsageService usage, string saved)
        {
            if (!http.Request.IsDatastarRequest)
            {
                return HttpRedirects.SeeOther("/usage?tab=settings&saved=" + saved);
            }

            var context = await AccountRequest.RequireAsync(http, sessions, accounts) ?? throw new AccountAccessException("Sign in again.");
            var tab = http.Request.Query["tab"].ToString() == "overview" ? "overview" : "settings";
            return UsageSelection.TryFor(context, http.Request.Query["project"].ToString(), http.Request.Query["database"].ToString(), http.Request.Query["range"].ToString()) switch
            {
                UsageSelection selection => await PatchAsync(http, usage, selection, tab, saved),
                NotFound failure => ErrorResults.ToResult(http, failure),
                Forbidden failure => ErrorResults.ToResult(http, failure),
            };
        }

        private static async Task<IResult> PatchAsync(HttpContext http, UsageService usage, UsageSelection selection, string tab, string saved)
        {
            var page = await usage.LoadAsync(ShellModel.For(http, selection.Context, "Usage"), selection, tab, saved, http.RequestAborted);
            await DatastarEvents.PatchElementsAsync(http, UsagePageView.Create(page));
            return Results.Empty;
        }
    }
}
