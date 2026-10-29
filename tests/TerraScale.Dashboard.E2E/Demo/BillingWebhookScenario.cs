using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Stripe;
using TerraScale.Dashboard.Features.Billing;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.E2E
{
    /// <summary>Creates signed provider-shaped webhook events from a real test billing binding.</summary>
    internal static class BillingWebhookScenario
    {
        public static void MapRoutes(WebApplication app)
        {
            app.MapPost("/__test/billing/webhook/event", async (HttpContext http, DashboardSessions sessions,
                AccountService accounts, StripeBilling billing) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.BadRequest();
                }

                if (context.Role != "owner")
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                var liveMode = form["liveMode"] == "true";
                var binding = await billing.ReadBindingAsync(context, http.RequestAborted);
                if (binding is not { CustomerId.Length: > 0, SubscriptionId.Length: > 0 })
                {
                    return Results.BadRequest();
                }

                var body = JsonSerializer.Serialize(new
                {
                    id = "evt_test_billing_webhook_stale",
                    @object = "event",
                    api_version = StripeConfiguration.ApiVersion,
                    created = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(),
                    data = new
                    {
                        @object = new
                        {
                            id = binding.SubscriptionId,
                            @object = "subscription",
                            customer = binding.CustomerId,
                            status = "canceled",
                            livemode = liveMode,
                            metadata = new { organization_id = context.Organization!.Id, plan = "free" }
                        }
                    },
                    livemode = liveMode,
                    pending_webhooks = 1,
                    request = (object?)null,
                    type = "customer.subscription.updated"
                });
                var signature = EventUtility.GenerateSignatureHeader(body, "whsec_demo");
                return Results.Json(new { body, signature });
            });
        }

    }
}
