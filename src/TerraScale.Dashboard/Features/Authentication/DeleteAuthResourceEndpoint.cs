using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Authentication;

public sealed class DeleteAuthResourceEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/auth-management/{section}/{id}/delete");
        Summary("Deletes or revokes an authentication console resource");
        Tags("Authentication");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var section = HttpContext.Request.RouteValues["section"]?.ToString() ?? "";
        var id = HttpContext.Request.RouteValues["id"]?.ToString() ?? "";
        var definition = AuthSection.Find(section);
        if (definition is null || !definition.CanDelete || !store.RemoveAuthResource(section, id)) return Task.FromResult<IResult>(Results.NotFound());
        return Task.FromResult<IResult>(Results.Redirect(definition.Href + "?toast=" + Uri.EscapeDataString(definition.DeleteLabel == "Revoke" ? "Access revoked." : "Resource deleted.")));
    }
}
