using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Tests;

/// <summary>Calls the platform management API (<c>/v1/terrascale/…</c>, TS-598) as signed-in users.</summary>
public static class PlatformCalls
{
    public const string Organizations = "/v1/terrascale/organizations";

    /// <summary>A fresh user signed in through the browser flow, with its sign-in access token.</summary>
    public static async Task<(UserRecord User, string Token)> SignInUserAsync(this OidcHarness oidc, string email)
    {
        var (user, _, tokens) = await oidc.SignInAndRedeemAsync(email);
        return (user, tokens.GetProperty("access_token").GetString()!);
    }

    /// <summary>Sends <paramref name="body"/> (as JSON, unless null) with <paramref name="token"/> as the bearer.</summary>
    public static async Task<HttpResponseMessage> CallAsync(this OidcHarness oidc, HttpMethod method, string path, string? token, object? body = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        await oidc.AttachStepUpAsync(request, token);
        return await (client ?? oidc.Client).SendAsync(request);
    }

    /// <summary>The JSON answer of a call that must succeed with <paramref name="status"/>.</summary>
    public static async Task<JsonElement> OkAsync(this OidcHarness oidc, HttpMethod method, string path, string token, object? body = null, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        using var response = await oidc.CallAsync(method, path, token, body);
        var text = await response.Content.ReadAsStringAsync();
        return response.StatusCode == status
            ? text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text)
            : throw new InvalidOperationException($"{method} {path} answered {(int)response.StatusCode}: {text}");
    }

    /// <summary>The status and error code of a call.</summary>
    public static async Task<(int Status, string? Code)> FailAsync(this OidcHarness oidc, HttpMethod method, string path, string? token, object? body = null)
    {
        using var response = await oidc.CallAsync(method, path, token, body);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length > 0 && JsonSerializer.Deserialize<JsonElement>(text) is { ValueKind: JsonValueKind.Object } json
            && json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error.GetProperty("code").GetString() : null);
    }

    /// <summary>Creates an organization owned by the caller and returns its id.</summary>
    public static async Task<string> CreateOrganizationAsync(this OidcHarness oidc, string token, string name = "Acme") =>
        (await oidc.OkAsync(HttpMethod.Post, Organizations, token, new { name }, System.Net.HttpStatusCode.Created)).GetProperty("id").GetString()!;

    /// <summary>Creates a project in the organization and returns its id.</summary>
    public static async Task<string> CreateProjectAsync(this OidcHarness oidc, string token, string organizationId, string name) =>
        (await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organizationId}/projects", token, new { name }, System.Net.HttpStatusCode.Created)).GetProperty("id").GetString()!;

    /// <summary>POSTs a form to the token endpoint, with HTTP Basic credentials when <paramref name="clientId"/> is set.</summary>
    public static Task<HttpResponseMessage> PostTokenAsync(this OidcHarness oidc, Dictionary<string, string> form, string? clientId, string? secret, HttpClient? client = null, string tenant = "terrascale")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/{tenant}/oauth/token") { Content = new FormUrlEncodedContent(form) };
        if (clientId is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        }

        return (client ?? oidc.Client).SendAsync(request);
    }

    /// <summary>Exchanges an API key string for a token (client credentials, HTTP Basic).</summary>
    public static Task<HttpResponseMessage> ApiKeyTokenAsync(this OidcHarness oidc, string key, Dictionary<string, string>? parameters = null)
    {
        var form = new Dictionary<string, string>(parameters ?? []) { ["grant_type"] = "client_credentials" };
        return oidc.PostTokenAsync(form, key[..26], key);
    }

    /// <summary>Exchanges a user's sign-in access token as the dashboard (RFC 8693).</summary>
    public static Task<HttpResponseMessage> ExchangeAsync(this OidcHarness oidc, string subjectToken, Dictionary<string, string> parameters, HttpClient? client = null)
    {
        var form = new Dictionary<string, string>(parameters)
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
        };
        return oidc.PostTokenAsync(form, "01M4EQ2FRJ8S8B29E8TQVWNW4J", OidcHarness.ClientSecret, client);
    }

    /// <summary>
    /// Validates a token as TerraBase does (ES256, the issuer's JWKS, exact issuer and audience, at+jwt), expiry against
    /// <paramref name="clock"/> when a test runs ts-auth on its own clock.
    /// </summary>
    public static async Task<JsonWebToken> ValidateAsync(this OidcHarness oidc, string token, string audience, TimeProvider? clock = null)
    {
        var keys = new JsonWebKeySet(await oidc.Client.GetStringAsync("/terrascale/.well-known/jwks.json"));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = OidcHarness.Issuer,
            ValidAudience = audience,
            ValidTypes = ["at+jwt"],
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKeys = keys.GetSigningKeys(),
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = clock is null ? null : (_, expires, _, _) => expires is { } end && clock.GetUtcNow().UtcDateTime < end,
        });
        return result.IsValid ? (JsonWebToken)result.SecurityToken : throw new InvalidOperationException($"Token rejected: {result.Exception?.GetType().Name}");
    }

    /// <summary>Adds a member directly, as an accepted invitation does.</summary>
    public static Task<bool> AddMemberAsync(this OidcHarness oidc, string organizationId, string userId, string role) =>
        oidc.Server.App.Services.GetRequiredService<PlatformDirectory>().AddMemberAsync(organizationId, userId, role, CancellationToken.None);

    /// <summary>Grants a project role directly, as an accepted project invitation does.</summary>
    public static Task<bool> GrantAsync(this OidcHarness oidc, string projectId, string userId, string role) =>
        oidc.Server.App.Services.GetRequiredService<PlatformDirectory>().GrantAsync(projectId, userId, role, CancellationToken.None);
}
