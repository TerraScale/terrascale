using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed record CliApprovePageModel(
    bool Authenticated,
    string? Code,
    string? Error,
    string? Outcome,
    string[] Scopes,
    string[] Resources,
    string AccountEmail) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class CliApprovePageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/cli/approve");
        Summary("Renders the CLI device approval page");
        Tags("Pages");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var raw = query["user_code"].ToString();
        var authenticated = DashboardAuthMiddleware.IsAuthenticated(HttpContext);
        string? error = null;
        string? code = null;

        if (raw.Length > 0)
        {
            if (raw != store.DemoCliUserCode)
            {
                error = "Enter the valid code displayed in your terminal.";
            }
            else
            {
                code = raw;
            }
        }

        var model = new CliApprovePageModel(
            Authenticated: authenticated,
            Code: code,
            Error: error ?? query["error"].FirstOrDefault(),
            Outcome: query["outcome"].FirstOrDefault(),
            Scopes: code is null ? [] : [.. store.DemoCliReview.Scopes],
            Resources: code is null ? [] : [.. store.DemoCliReview.Resources],
            AccountEmail: store.CurrentUser.Email);
        return Task.FromResult<IResult>(CliApproveSlice.Create(model));
    }
}
