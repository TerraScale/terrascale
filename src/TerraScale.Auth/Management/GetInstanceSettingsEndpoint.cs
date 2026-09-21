namespace TerraScale.Auth.Management;

using TerraScale.Auth.Api;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Endpoints;

internal sealed class GetInstanceSettingsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/instance", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();
        var instance = context.Instance;
        return Results.Ok(new { id = instance.Id, slug = instance.Slug, name = instance.Name, logo_url = instance.LogoUrl, theme_color = instance.ThemeColor, created_at = instance.CreatedAt });
    }
}
