using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Tests;

/// <summary>Seeds approved proof state for tests of downstream operations. Step-up security tests use raw HTTP instead.</summary>
public static class StepUpFixtures
{
    private static readonly JsonSerializerOptions Metadata = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static async Task<string?> ApprovedProofAsync(this OidcHarness oidc, string token, string purpose, string audience = "ts-auth", IReadOnlyList<CredentialBinding>? bindings = null, string? organizationId = null)
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

        var id = AuthIds.New();
        var proof = id + "." + Tokens.RandomToken(43);
        var record = new StepUpRecord(user.InstanceId, id, user.Id, user.AuthEpoch, user.AuthStamp, Tokens.Sha256Hex(proof), purpose, audience,
            "https://dash.example.test/auth/step-up/callback", services.GetRequiredService<TimeProvider>().GetUtcNow().AddMinutes(5),
            PrimaryVerified: true, Approved: true, Bindings: bindings ?? user.AuthenticationBindings, OrganizationId: organizationId);
        await services.GetRequiredService<AuthData>().SaveAsync(Change.Insert(record), CancellationToken.None);
        return proof;
    }

    public static async Task AttachStepUpAsync(this OidcHarness oidc, HttpRequestMessage request, string? token)
    {
        var path = request.RequestUri!.OriginalString.Split('?')[0];
        var purpose = request.Method == HttpMethod.Delete && path == "/v1/terrascale/account" ? "delete_account"
            : request.Method == HttpMethod.Post && path.EndsWith("/api-keys", StringComparison.Ordinal) ? "create_api_key"
            : request.Method == HttpMethod.Post && path.Contains("/api-keys/", StringComparison.Ordinal) && path.EndsWith("/rotate", StringComparison.Ordinal) ? "rotate_api_key"
            : request.Method == HttpMethod.Post && path.EndsWith("/clients", StringComparison.Ordinal) ? "create_oauth_client"
            : request.Method == HttpMethod.Post && path.EndsWith("/webhook-endpoints", StringComparison.Ordinal) ? "create_webhook_endpoint"
            : request.Method == HttpMethod.Post && path.Contains("/webhook-endpoints/", StringComparison.Ordinal) && path.EndsWith("/rotate-secret", StringComparison.Ordinal) ? "rotate_webhook_secret"
            : request.Method == HttpMethod.Delete && path.StartsWith("/v1/terrascale/organizations/", StringComparison.Ordinal) && path.Count(character => character == '/') == 4 ? "delete_organization"
            : request.Method == HttpMethod.Post && path.EndsWith("/ownership-transfer", StringComparison.Ordinal) ? "start_ownership_transfer"
            : request.Method == HttpMethod.Put && path.Contains("/clients/", StringComparison.Ordinal) ? "update_oauth_client"
            : request.Method == HttpMethod.Post && path.Contains("/clients/", StringComparison.Ordinal) && path.EndsWith("/secret/rotate", StringComparison.Ordinal) ? "rotate_oauth_client"
            : request.Method == HttpMethod.Delete && path.StartsWith("/v1/terrascale/account/passkeys/", StringComparison.Ordinal) ? "delete_passkey"
            : request.Method == HttpMethod.Post && path == "/v1/terrascale/account/email-change" ? "change_email"
            : request.Method == HttpMethod.Post && path == "/v1/terrascale/account/passkeys/verify" ? "add_passkey" : null;
        if (token is not null && purpose is not null && await oidc.ApprovedProofAsync(token, purpose,
                organizationId: purpose is "delete_organization" or "start_ownership_transfer" ? path.Split('/')[4] : null) is { } proof)
        {
            request.Headers.Add(StepUpProofs.Header, proof);
        }
    }
}
