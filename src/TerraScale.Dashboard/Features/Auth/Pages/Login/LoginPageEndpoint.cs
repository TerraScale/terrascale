using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth.Pages.Login;

public sealed class LoginPageEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/login");
        Summary("Renders the sign-in page");
        Tags("Pages");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(LoginSlice.Create(new LoginPageModel { Error = HttpContext.Request.Query["error"].FirstOrDefault() }));
}
