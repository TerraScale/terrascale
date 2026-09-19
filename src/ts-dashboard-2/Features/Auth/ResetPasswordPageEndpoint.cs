using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed class ResetPasswordPageEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/reset-password");
        Summary("Renders the password reset page");
        Tags("Pages");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var model = new ResetPasswordPageModel(
            Error: query["error"].FirstOrDefault(),
            Sent: query["sent"] == "1",
            Reset: query["token"].ToString() is { Length: > 0 },
            Updated: query["updated"] == "1");
        return Task.FromResult<IResult>(ResetPasswordSlice.Create(model));
    }
}
