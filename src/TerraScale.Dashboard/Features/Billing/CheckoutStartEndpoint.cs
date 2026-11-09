using TerraScale.TerraBase.Contracts;
using Stripe;
using TerraScale.Dashboard.Platform.Contracts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Common.Validation;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Endpoints;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed class CheckoutStartEndpoint(DashboardSessions sessions, AccountService accounts, StripeBilling billing) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Post("/billing/checkout");
            Metadata(new IntegrationEndpointGroup("integration"));
            Name("startDashboardCheckout");
            Tags("Billing");
            Summary("Start or resume a bound Stripe checkout");
            Description("Requires the current session, organization-owner permission and a same-origin CSRF form token. An unavailable provider returns 503; return to billing to resume or cancel rather than automatically retrying a mutation.");
            Accepts<CheckoutRequest>("application/x-www-form-urlencoded");
            Metadata(new ProducesResponseTypeMetadata(303, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(401, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(403, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(422, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(503, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(503, typeof(void), ["text/html"]));
        }

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
                return Results.StatusCode(403);
            }

            try { return HttpRedirects.SeeOther(await billing.StartCheckoutAsync(context, form["plan"].ToString(), form["interval"].ToString(), form["return"].ToString(), http.RequestAborted)); }
            catch (Exception error) when (error is AccountAccessException or OnboardingValidationException)
            {
                if (DatabaseEndpoints.WantsHtml(http))
                {
                    return HttpRedirects.SeeOther("/organization?tab=billing&payment=blocked");
                }

                return Results.Problem(error.Message, statusCode: error is AccountAccessException access ? access.StatusCode : 422);
            }
            catch (Exception error) when (error is StripeException or TerraBaseException or HttpRequestException or IOException ||
                error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<CheckoutStartEndpoint>().CheckoutForOrganizationIsUnavailable(context.Organization?.Id, error.GetType().Name, BillingMutations.Describe(error));
                if (!DatabaseEndpoints.WantsHtml(http))
                {
                    return Results.Problem("Checkout is temporarily unavailable. Return to billing to reconcile before trying again.", statusCode: 503);
                }

                var page = BillingCheckoutUnavailable.Create(new DocumentModel("Checkout unavailable", DocumentModel.NonceFor(http)));
                page.StatusCode = 503;
                return (IResult)page;
            }
        }
    }
}
