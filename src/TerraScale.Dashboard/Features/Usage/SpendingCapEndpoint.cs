using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Usage
{
    public sealed class SpendingCapEndpoint(DashboardSessions sessions, AccountService accounts, SpendingService spending) : EndpointWithoutRequest<IResult>
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

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
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
                return HttpRedirects.SeeOther("/usage?tab=settings&saved=invalid");
            }

            try { await spending.SetCapAsync(context, capCents, http.RequestAborted); }
            catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
            return HttpRedirects.SeeOther("/usage?tab=settings&saved=ok");
        }
    }
}
