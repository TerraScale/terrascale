using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectSessionPolicyTests(TerraBaseFixture fixture)
{





    [Test]
    public async Task Project_lifetimes_apply_on_issuance_resolution_and_rotation_without_affecting_other_tenants()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (ownerToken, project) = await ProjectAsync(oidc);
        var other = await oidc.CreateProjectAsync(ownerToken, await oidc.CreateOrganizationAsync(ownerToken), "Other project");
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var user = (await users.CreateAsync(project, new Faker().Internet.Email()))!;
        var platform = await sessions.CreateAsync((await users.CreateAsync(OidcIssuer.InstanceId, new Faker().Internet.Email()))!);
        var bystander = await sessions.CreateAsync((await users.CreateAsync(other, user.Email))!);
        await ConfigureAsync(oidc, ownerToken, project, 1200);
        var issued = await sessions.CreateAsync(user);
        await Assert.That(issued.Session.ExpiresAt).IsEqualTo(issued.Session.CreatedAt.AddSeconds(1200));

        clock.UtcNow += TimeSpan.FromSeconds(120);
        var rotated = (await sessions.RotateAsync(project, issued.Token))!;
        await Assert.That(rotated.Session.CreatedAt).IsEqualTo(issued.Session.CreatedAt);
        await Assert.That(rotated.Session.ExpiresAt).IsEqualTo(issued.Session.ExpiresAt);
        await ConfigureAsync(oidc, ownerToken, project, 300);
        var effective = (await sessions.ResolveAsync(project, rotated.Token))!.Value.Session;
        await Assert.That(effective.ExpiresAt).IsEqualTo(issued.Session.CreatedAt.AddSeconds(300));
        rotated = (await sessions.RotateAsync(project, rotated.Token))!;
        await Assert.That(rotated.Session.CreatedAt).IsEqualTo(issued.Session.CreatedAt);
        await Assert.That(rotated.Session.ExpiresAt).IsEqualTo(effective.ExpiresAt);
        var fresh = await sessions.CreateAsync(user);
        await Assert.That(fresh.Session.ExpiresAt).IsEqualTo(clock.UtcNow.AddSeconds(300));

        clock.UtcNow = effective.ExpiresAt;
        await Assert.That(await sessions.ResolveAsync(project, rotated.Token)).IsNull();
        await Assert.That(await sessions.RotateAsync(project, rotated.Token)).IsNull();
        await Assert.That(await sessions.RevokeAsync(project, rotated.Token)).IsTrue();
        await Assert.That(await sessions.ResolveAsync(project, fresh.Token)).IsNotNull();
        await Assert.That(await sessions.ResolveAsync(other, fresh.Token)).IsNull();
        await Assert.That((await sessions.ResolveAsync(other, bystander.Token))!.Value.Session.ExpiresAt).IsEqualTo(bystander.Session.CreatedAt + Sessions.Lifetime);
        await Assert.That((await sessions.ResolveAsync(OidcIssuer.InstanceId, platform.Token))!.Value.Session.ExpiresAt).IsEqualTo(platform.Session.CreatedAt + Sessions.Lifetime);
    }








    private static async Task<(string OwnerToken, string Project)> ProjectAsync(OidcHarness oidc)
    {
        var (_, token) = await oidc.SignInUserAsync(new Faker().Internet.Email().ToLowerInvariant());
        return (token, await oidc.CreateProjectAsync(token, await oidc.CreateOrganizationAsync(token), "Policy project"));
    }

    private static async Task ConfigureAsync(OidcHarness oidc, string token, string project, int seconds, params string[] redirects) =>
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/settings", token,
            new { configuration = new { redirect_urls = redirects, session_lifetime_seconds = seconds } });


}
