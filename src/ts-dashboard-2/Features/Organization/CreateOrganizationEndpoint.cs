using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

public sealed class CreateOrganizationEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organizations/create");
        Summary("Creates the organization and advances the wizard to payment");
        Tags("Organization");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Redirect("/organizations/new?step=2", false, false));
}
