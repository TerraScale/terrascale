namespace TerraScale.Auth.Connections;

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Connections.ConnectionFlow;

internal sealed class DeleteConnectionEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/connections/{id}", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string id, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var connection = await store.GetConnectionAsync(id, ct);
        if (connection is null || connection.InstanceId != context.Instance.Id)
            return ApiResults.NotFound("connection_not_found", "no connection with this id in this instance");
        await store.DeleteConnectionAsync(id, ct);
        return Results.NoContent();
    }
}
