namespace TerraScale.Auth.OAuth;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.OAuth.OAuthSupport;

internal sealed class ListOAuthClientsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/oauth-clients", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, InstanceResolver resolver,
            AuthStore store, TerraScaleClient sdk, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context)
            return ApiResults.Unauthorized();
        if (context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var page = await sdk.Documents.ListAsync(store.Database, "oauth_clients", 200, null, ct);
        var clients = page.Items
            .Select(item => item.Value.Deserialize<OAuthClient>(Json))
            .OfType<OAuthClient>()
            .Where(client => client.InstanceId == context.Instance.Id)
            .Select(ClientView)
            .ToList();
        return Results.Ok(new { clients });
    }
}
