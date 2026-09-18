using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Access;

public sealed record InvitationPageModel(
    string InvitationId,
    string? Error,
    string? Outcome) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class InvitationPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/invitations/{invitationId}");
        Summary("Renders the organization invitation response page");
        Tags("Pages");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var id = HttpContext.Request.RouteValues["invitationId"] as string ?? "";
        var query = HttpContext.Request.Query;
        var invitation = store.Invitations.FirstOrDefault(i =>
            i.Id == id || (i.Token is { Length: > 0 } && i.Token == id));
        var model = new InvitationPageModel(
            InvitationId: invitation?.Id ?? id,
            Error: query["error"].FirstOrDefault(),
            Outcome: query["outcome"].FirstOrDefault());
        return Task.FromResult<IResult>(InvitationSlice.Create(model));
    }
}
