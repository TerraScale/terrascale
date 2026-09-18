using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

public sealed class RequestOrganizationDeletionEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/deletion/request");
        Summary("Records the organization deletion request");
        Tags("Organization");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Redirect("/organization", false, false));
}
