using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>Real SDK persistence, concurrent bootstrap and email-code access to the seeded organization.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PlatformBootstrapTests(TerraBaseFixture fixture)
{
    private static readonly string[] ProjectIds = [PlatformBootstrap.AuthProjectId, PlatformBootstrap.DashboardProjectId];
    private static readonly string[] ProjectNames = ["ts-auth", "ts-dashboard"];

    [Test]
    public async Task Seeded_owner_signs_in_by_email_code_and_reads_both_projects()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var session = await oidc.SignInAsync(AuthTestServer.BootstrapOwnerEmail);
        var (verifier, challenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var token = (await OidcHarness.JsonAsync(redeemed)).GetProperty("access_token").GetString()!;
        var organizations = await oidc.OkAsync(HttpMethod.Get, PlatformCalls.Organizations, token);
        await Assert.That(organizations.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(organizations.GetProperty("items")[0].GetProperty("name").GetString()).IsEqualTo("TerraScale");
        await Assert.That(organizations.GetProperty("items")[0].GetProperty("role").GetString()).IsEqualTo(Roles.Owner);
        var projects = await oidc.OkAsync(HttpMethod.Get, $"{PlatformCalls.Organizations}/{PlatformBootstrap.OrganizationId}/projects", token);
        await Assert.That(projects.GetProperty("items").EnumerateArray().Select(project => project.GetProperty("id").GetString()!))
            .IsEquivalentTo(ProjectIds);
        await Assert.That(projects.GetProperty("items").EnumerateArray().Select(project => project.GetProperty("name").GetString()!))
            .IsEquivalentTo(ProjectNames);
    }

}
