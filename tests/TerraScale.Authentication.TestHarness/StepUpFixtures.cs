using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Tests;

/// <summary>Seeds approved proof state for tests of downstream operations. Step-up security tests use raw HTTP instead.</summary>
public static class StepUpFixtures
{
    private static readonly JsonSerializerOptions Metadata = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static async Task<string?> ApprovedProofAsync(this OidcHarness oidc, string token, string purpose, string audience = "ts-auth", IReadOnlyList<CredentialBinding>? bindings = null)
    {
        if (!new JsonWebTokenHandler().CanReadToken(token))
        {
            return null;
        }

        var services = oidc.Server.App.Services;
        var subject = new JsonWebToken(token).Subject;
        var user = await services.GetRequiredService<Users>().GetAsync("terrascale", subject);
        if (user is null)
        {
            return null;
        }

        var store = services.GetRequiredService<AuthStore>();
        var id = AuthStore.NewId();
        var proof = id + "." + Tokens.RandomToken(43);
        var record = new
        {
            instanceId = user.InstanceId,
            id,
            userId = user.Id,
            authEpoch = user.AuthEpoch,
            authStamp = user.AuthStamp,
            tokenHash = Tokens.Sha256Hex(proof),
            purpose,
            audience,
            returnUri = "https://dash.example.test/auth/step-up/callback",
            expiresAt = services.GetRequiredService<TimeProvider>().GetUtcNow().AddMinutes(5),
            primaryVerified = true,
            approved = true,
            consumed = false,
            mailRequested = false,
            bindings = bindings ?? user.AuthenticationBindings,
        };
        await store.CreateAsync(AuthStore.StepUpProofCollection, user.InstanceId, id,
            JsonSerializer.SerializeToElement(record, Metadata));
        return proof;
    }

    public static async Task AttachStepUpAsync(this OidcHarness oidc, HttpRequestMessage request, string? token)
    {
        var path = request.RequestUri!.OriginalString.Split('?')[0];
        var purpose = request.Method == HttpMethod.Delete && path == "/v1/terrascale/account" ? "delete_account"
            : request.Method == HttpMethod.Post && path.EndsWith("/api-keys", StringComparison.Ordinal) ? "create_api_key"
            : request.Method == HttpMethod.Post && path.Contains("/api-keys/", StringComparison.Ordinal) && path.EndsWith("/rotate", StringComparison.Ordinal) ? "rotate_api_key"
            : request.Method == HttpMethod.Post && path.EndsWith("/clients", StringComparison.Ordinal) ? "create_oauth_client" : null;
        if (token is not null && purpose is not null && await oidc.ApprovedProofAsync(token, purpose) is { } proof)
        {
            request.Headers.Add(StepUpProofs.Header, proof);
        }
    }
}
