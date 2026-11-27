using TerraScale.Dashboard.Platform.Contracts;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Accounts
{
    public sealed class DashboardIdentityEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            AuthenticateInHandler();
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
            var required = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (!AccountRequest.TryResolveApi(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            return TypedResults.Json(new AccountIdentityResponse(context.UserId, context.Account.Id,
                context.Account.Name, context.Account.Email, context.Account.Phone, context.Account.Locale,
                context.Account.OnboardingCompleted, context.Organization?.Id, context.Project?.Id, context.Role),
                AccountContractJson.Default.AccountIdentityResponse);
        }
    }
}
