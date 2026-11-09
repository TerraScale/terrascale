using TerraScale.Dashboard.Platform.Contracts;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Accounts
{
    public sealed class DashboardIdentityEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Get("/api/v1/auth/me");
            Metadata(new IntegrationEndpointGroup("integration"));
            Name("getDashboardIdentity");
            Tags("Session");
            Summary("Read the current dashboard session and workspace");
            Metadata(new ProducesResponseTypeMetadata(200, typeof(AccountIdentityResponse), ["application/json"]));
            Metadata(new ProducesResponseTypeMetadata(401, typeof(void)));
        }

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            return Results.Json(new AccountIdentityResponse(context.UserId, context.Account.Id,
                context.Account.Name, context.Account.Email, context.Account.Phone, context.Account.Locale,
                context.Account.OnboardingCompleted, context.Organization?.Id, context.Project?.Id, context.Role),
                AccountContractJson.Default.AccountIdentityResponse);
        }
    }
}
