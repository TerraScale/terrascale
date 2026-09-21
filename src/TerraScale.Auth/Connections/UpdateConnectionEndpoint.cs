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

internal sealed class UpdateConnectionEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPatch("/v1/{slug}/connections/{id}", HandleAsync);

    private static async Task<IResult> HandleAsync(
        string slug, string id, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var (body, bodyFailure) = await ReadBodyAsync<UpdateConnectionBody>(request, ct);
        if (body is null)
            return bodyFailure!;

        var connection = await store.GetConnectionAsync(id, ct);
        if (connection is null || connection.InstanceId != context.Instance.Id)
            return ApiResults.NotFound("connection_not_found", "no connection with this id in this instance");

        var updated = connection;
        if (body.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return ApiResults.BadRequest("invalid_request", "name must not be empty");
            updated = updated with { Name = body.Name.Trim() };
        }
        if (body.Enabled is { } enabled)
            updated = updated with { Enabled = enabled };
        if (body.ClientSecret is not null)
        {
            if (string.IsNullOrWhiteSpace(body.ClientSecret))
                return ApiResults.BadRequest("invalid_request", "client_secret must not be empty");
            updated = updated with { ClientSecretBox = SecretBox.Encrypt(options.MasterKey, Encoding.UTF8.GetBytes(body.ClientSecret)) };
        }

        await store.UpsertConnectionAsync(updated, ct);
        return Results.Json(ConnectionView(updated));
    }

    private sealed record UpdateConnectionBody(
        bool? Enabled,
        string? Name,
        [property: JsonPropertyName("client_secret")] string? ClientSecret);
}
