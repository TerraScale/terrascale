using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Authentication.Endpoints;

public sealed class AuthResourceEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/auth-management/{section}");
        Summary("Saves an authentication console resource");
        Tags("Authentication");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var section = HttpContext.Request.RouteValues["section"]?.ToString() ?? "";
        var definition = AuthSection.Find(section);
        if (definition is null || !definition.CanCreate) return Results.NotFound();
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var id = form["id"].ToString();
        if (id.Length > 0 && (!definition.CanEdit || !store.AuthInstance.Resources.Any(resource => resource.Section == section && resource.Id == id))) return Results.NotFound();
        var values = definition.Fields.ToDictionary(field => field.Key, field => form[field.Key].ToString().Trim(), StringComparer.OrdinalIgnoreCase);
        HttpContext.Response.Headers.CacheControl = "no-store";
        lock (store.AuthInstance)
        {
            if (AuthResourceValidation.Validate(definition, values, store.AuthInstance, id) is { } error)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                var model = AuthenticationPageModel.From(store, definition.Href, section, error: error, id: id, create: id.Length == 0) with { FormValues = values };
                return AuthenticationSlice.Create(model);
            }
            store.SaveAuthResource(section, values, id.Length == 0 ? null : id);
        }
        return Results.Redirect(definition.Href + "?toast=" + Uri.EscapeDataString(id.Length == 0 ? "Resource created." : "Changes saved."));
    }
}
