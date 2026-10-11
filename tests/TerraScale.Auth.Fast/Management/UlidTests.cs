using System.Net;
using TerraScale.Auth.Tests;
using TerraScale.DotNet;
using static TerraScale.Auth.Tests.PlatformCalls;

namespace TerraScale.Auth.Fast.Management;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class UlidTests(TerraBaseFixture fixture)
{
    private static readonly string[] ReadOnlyScopes = ["documents:read"];

    [Test]
    public async Task Generated_entities_and_signed_claims_use_canonical_ulids()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = "https://gru.api.terrascale.tech");
        var (user, token) = await oidc.SignInUserAsync("ulid@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "ULID project");
        var invitation = await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/invitations", token,
            new { email = "invitee@example.test", role = "member" }, HttpStatusCode.Created);
        var created = await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/projects/{project}/api-keys", token,
            new { name = "ULID client", scopes = ReadOnlyScopes }, HttpStatusCode.Created);
        var keyId = created.GetProperty("api_key").GetProperty("id").GetString()!;

        foreach (var id in new[] { user.Id, organization, project, keyId, invitation.GetProperty("invitation").GetProperty("id").GetString()! })
        {
            await Assert.That(TerraScaleIds.IsValid(id)).IsTrue();
        }

        var signIn = await oidc.ValidateAsync(token, OidcHarness.Issuer);
        foreach (var name in new[] { "sub", "oi_au_id", "oi_tkn_id" })
        {
            await Assert.That(TerraScaleIds.IsValid(signIn.GetClaim(name).Value)).IsTrue();
        }

        using var granted = await oidc.ApiKeyTokenAsync(created.GetProperty("key").GetString()!);
        await Assert.That(granted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var claims = await oidc.ValidateAsync((await OidcHarness.JsonAsync(granted)).GetProperty("access_token").GetString()!, "https://gru.api.terrascale.tech");
        await Assert.That(claims.GetClaim("sub").Value).IsEqualTo(keyId);
        await Assert.That(claims.GetClaim("org_id").Value).IsEqualTo(organization);
        await Assert.That(claims.GetClaim("project_id").Value).IsEqualTo(project);

        foreach (var invalid in new[] { organization.ToLowerInvariant(), "org_" + organization, "abcdef01-2345-6789-abcd-ef0123456789", "8" + organization[1..], "I" + organization[1..] })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{invalid}", token)).IsEqualTo((404, "not_found"));
        }
    }
}
