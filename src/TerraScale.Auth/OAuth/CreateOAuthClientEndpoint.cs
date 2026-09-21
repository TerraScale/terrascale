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

internal sealed class CreateOAuthClientEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/oauth-clients", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, InstanceResolver resolver,
            AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context)
            return ApiResults.Unauthorized();
        if (context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var body = await request.ReadFromJsonAsync<CreateOAuthClientRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Name) || body.RedirectUris is not { Length: > 0 })
            return ApiResults.BadRequest("invalid_request", "name and redirect_uris are required");
        foreach (var uri in body.RedirectUris)
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
                return ApiResults.BadRequest("invalid_request", "redirect_uris must be absolute http(s) urls");

        var authMethod = body.TokenEndpointAuthMethod is { Length: > 0 } ? body.TokenEndpointAuthMethod : (body.Public ? "none" : "client_secret_basic");
        if (authMethod is not ("client_secret_basic" or "client_secret_post" or "none"))
            return ApiResults.BadRequest("invalid_request", "token_endpoint_auth_method must be client_secret_basic, client_secret_post, or none");

        // The secret is returned exactly once; only its SHA-256 is stored.
        string? secret = body.Public || authMethod == "none" ? null : SecureTokens.RandomToken(40);
        var clientId = SecureTokens.RandomToken(24);
        var client = new OAuthClient
        {
            Id = "clt_" + SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            ClientId = clientId,
            ClientKey = $"{context.Instance.Id}:{clientId}",
            Name = body.Name.Trim(),
            ClientSecretHash = secret is null ? null : SecureTokens.Sha256Hex(secret),
            RedirectUris = body.RedirectUris,
            TokenEndpointAuthMethod = authMethod,
            Public = body.Public || authMethod == "none",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await store.CreateOAuthClientAsync(client, ct);
        return Results.Ok(new { client = ClientView(client), client_secret = secret });
    }
}
