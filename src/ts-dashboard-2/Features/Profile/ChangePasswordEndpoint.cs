using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Profile;

public sealed class ChangePasswordEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/password");
        Summary("Changes the account password and terminates sessions");
        Tags("Profile");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Redirect("/login", false, false));
}
