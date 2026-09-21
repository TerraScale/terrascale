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

internal sealed class ListPublicConnectionsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/connections", HandleAsync);

    // ---- Public sign-in surface --------------------------------------------------------

    private static async Task<IResult> HandleAsync(string slug, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound();
        var enabled = await store.ListConnectionsAsync(instance.Id, ct);
        return Results.Json(enabled.Where(c => c.Enabled).OrderBy(c => c.CreatedAt)
            .Select(c => new { id = c.Id, type = c.Type, name = c.Name }).ToList());
    }
}
