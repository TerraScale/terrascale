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

internal sealed class CreateConnectionEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/connections", HandleAsync);

    // ---- Management API ---------------------------------------------------------------

    private static async Task<IResult> HandleAsync(
        string slug, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var (body, bodyFailure) = await ReadBodyAsync<CreateConnectionBody>(request, ct);
        if (body is null)
            return bodyFailure!;
        if (string.IsNullOrWhiteSpace(body.Name))
            return ApiResults.BadRequest("invalid_request", "name is required");

        return body.Type switch
        {
            "oidc" => await CreateOidcAsync(context.Instance, body, options, store, ct),
            "saml" => await CreateSamlAsync(context.Instance, body, store, ct),
            _ => ApiResults.BadRequest("invalid_request", "type must be oidc or saml"),
        };
    }

    private static async Task<IResult> CreateOidcAsync(Instance instance, CreateConnectionBody body, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        if (body.IssuerUrl is null || !Uri.TryCreate(body.IssuerUrl, UriKind.Absolute, out var issuer) || issuer.Scheme is not ("http" or "https"))
            return ApiResults.BadRequest("invalid_request", "issuer_url must be an absolute http(s) URL");
        if (string.IsNullOrWhiteSpace(body.ClientId) || string.IsNullOrWhiteSpace(body.ClientSecret))
            return ApiResults.BadRequest("invalid_request", "client_id and client_secret are required");

        // Body endpoint overrides win over discovery metadata (tests pin them this way).
        var discovery = await OidcLogin.FetchDiscoveryAsync(issuer, ct);
        var authorization = body.AuthorizationEndpoint ?? discovery?.AuthorizationEndpoint;
        var token = body.TokenEndpoint ?? discovery?.TokenEndpoint;
        if (authorization is null || token is null)
            return ApiResults.BadRequest("discovery_failed", $"could not resolve authorization and token endpoints from {issuer.ToString().TrimEnd('/')}/.well-known/openid-configuration");

        var connection = new Connection
        {
            Id = "con_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            Type = "oidc",
            Name = body.Name!.Trim(),
            Enabled = true,
            OrgId = Optional(body.OrgId),
            IssuerUrl = issuer.ToString().TrimEnd('/'),
            ClientId = body.ClientId,
            ClientSecretBox = SecretBox.Encrypt(options.MasterKey, Encoding.UTF8.GetBytes(body.ClientSecret)),
            AuthorizationEndpoint = authorization,
            TokenEndpoint = token,
            UserinfoEndpoint = body.UserinfoEndpoint ?? discovery?.UserinfoEndpoint,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await store.CreateConnectionAsync(connection, ct);
        return Results.Json(ConnectionView(connection), statusCode: 201);
    }

    private static async Task<IResult> CreateSamlAsync(Instance instance, CreateConnectionBody body, AuthStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.IdpEntityId) || string.IsNullOrWhiteSpace(body.SsoRedirectUrl) || string.IsNullOrWhiteSpace(body.X509CertificatePem))
            return ApiResults.BadRequest("invalid_request", "idp_entity_id, sso_redirect_url, and x509_certificate_pem are required");
        if (!Uri.TryCreate(body.SsoRedirectUrl, UriKind.Absolute, out var sso) || sso.Scheme is not ("http" or "https"))
            return ApiResults.BadRequest("invalid_request", "sso_redirect_url must be an absolute http(s) URL");
        using var parsed = TryLoadCertificate(body.X509CertificatePem);
        if (parsed is null)
            return ApiResults.BadRequest("invalid_certificate", "x509_certificate_pem is not a parsable PEM certificate");

        var connection = new Connection
        {
            Id = "con_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            Type = "saml",
            Name = body.Name!.Trim(),
            Enabled = true,
            OrgId = Optional(body.OrgId),
            IdpEntityId = body.IdpEntityId,
            SsoRedirectUrl = body.SsoRedirectUrl,
            X509CertificatePem = body.X509CertificatePem,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await store.CreateConnectionAsync(connection, ct);
        return Results.Json(ConnectionView(connection), statusCode: 201);
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- Request bodies ------------------------------------------------------------------

    private sealed record CreateConnectionBody(
        string? Type,
        string? Name,
        [property: JsonPropertyName("org_id")] string? OrgId,
        [property: JsonPropertyName("issuer_url")] string? IssuerUrl,
        [property: JsonPropertyName("client_id")] string? ClientId,
        [property: JsonPropertyName("client_secret")] string? ClientSecret,
        // Accepted for API parity; the Connection record has no scopes field yet, so authorize
        // always requests "openid email profile". Persist when the model grows the field.
        string[]? Scopes,
        [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint,
        [property: JsonPropertyName("userinfo_endpoint")] string? UserinfoEndpoint,
        [property: JsonPropertyName("idp_entity_id")] string? IdpEntityId,
        [property: JsonPropertyName("sso_redirect_url")] string? SsoRedirectUrl,
        [property: JsonPropertyName("x509_certificate_pem")] string? X509CertificatePem);
}
