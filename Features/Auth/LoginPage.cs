using RazorSlices;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed record LoginPageModel(string? Error = null) : IPageModel
{
    public ShellModel Shell { get; init; } = null!; // login renders without the shell
}

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
