using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Platform;

namespace TerraScale.Auth.Tests;

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
    public static Task<HttpResponseMessage> CallAsync(this OidcHarness oidc, HttpMethod method, string path, string? token, object? body = null, HttpClient? client = null)
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

        return (client ?? oidc.Client).SendAsync(request);
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

    /// <summary>Adds a member directly, as an accepted invitation does.</summary>
    public static Task<bool> AddMemberAsync(this OidcHarness oidc, string organizationId, string userId, string role) =>
        oidc.Server.App.Services.GetRequiredService<PlatformDirectory>().AddMemberAsync(organizationId, userId, role, CancellationToken.None);

    /// <summary>Grants a project role directly, as an accepted project invitation does.</summary>
    public static Task<bool> GrantAsync(this OidcHarness oidc, string projectId, string userId, string role) =>
        oidc.Server.App.Services.GetRequiredService<PlatformDirectory>().GrantAsync(projectId, userId, role, CancellationToken.None);
}
