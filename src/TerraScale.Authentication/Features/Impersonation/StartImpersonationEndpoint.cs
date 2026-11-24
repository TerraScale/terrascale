using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Impersonation;

public sealed class StartImpersonationEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, Impersonations impersonations, StepUpProofs proofs)
    : ProjectTenantEndpoint(tenants, authentication)
{
    protected override bool PersonOnly => true;

    public override void Configure()
    {
        Post(Root + "/users/{user}/impersonations");
        this.Describe("start_impersonation", "Issue a single-use impersonation ticket",
            "Requires a person with the project admin role. Tickets last 60 seconds and bind one client; starting requires a start_impersonation step-up proof.",
            "Impersonation", typeof(StartImpersonationRequest), 201, typeof(ImpersonationTicket));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var person = caller.RequirePerson();
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(ImpersonationJson.Default.StartImpersonationRequest, cancellationToken);
        person = await proofs.ConsumeHeaderAsync(person, this.HttpContext.Request, "start_impersonation", project + ":" + this.RouteId("user"), cancellationToken);
        return await impersonations.StartAsync(person, project, this.RouteId("user"), request, cancellationToken) switch
        {
            ImpersonationTicket issued => TypedResults.Json(issued, ImpersonationJson.Default.ImpersonationTicket, statusCode: 201),
            AuthValidation error => ErrorResults.ToResult(error),
            AuthNotFound error => ErrorResults.ToResult(error),
            AuthForbidden error => ErrorResults.ToResult(error),
        };
    }
}
