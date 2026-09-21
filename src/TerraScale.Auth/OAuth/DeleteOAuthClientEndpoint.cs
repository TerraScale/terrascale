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

internal sealed class DeleteOAuthClientEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/oauth-clients/{id}", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string id, HttpRequest request,
            InstanceResolver resolver, AuthStore store, TerraScaleClient sdk, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context)
            return ApiResults.Unauthorized();
        if (context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        if (await store.GetOAuthClientAsync(id, ct) is not { } client || client.InstanceId != context.Instance.Id)
            return ApiResults.NotFound("not_found", "no such oauth client");
        await store.DeleteOAuthClientAsync(id, ct);
        return Results.NoContent();
    }
}
