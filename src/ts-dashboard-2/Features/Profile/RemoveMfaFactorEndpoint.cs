using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Profile;

public sealed class RemoveMfaFactorEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/mfa/remove");
        Summary("Removes the TOTP authenticator factor");
        Tags("Profile");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Redirect("/profile?tab=security", false, false));
}
