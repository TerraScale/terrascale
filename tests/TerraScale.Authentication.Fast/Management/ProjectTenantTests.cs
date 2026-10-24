using System.Globalization;
using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>The project tenant management API (<c>/v1/{project}/…</c>) over real HTTP on the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectTenantTests(TerraBaseFixture fixture)
{
    private readonly Faker faker = new();

    [Test]
    public async Task Viewers_read_admins_disable_and_outsiders_see_nothing()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (owner, ownerToken, project, root) = await this.ProjectAsync(oidc);
        var (viewer, viewerToken) = await oidc.SignInUserAsync(this.Email(2));
        var (_, outsiderToken) = await oidc.SignInUserAsync(this.Email(3));
        await Assert.That(await oidc.GrantAsync(project, viewer.Id, "viewer")).IsTrue();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var endUser = (await users.CreateAsync(project, this.Email(4)))!;
        var session = await sessions.CreateAsync(endUser);

        var found = await oidc.OkAsync(HttpMethod.Get, $"{root}/users?q={Uri.EscapeDataString(endUser.Email[..3])}", viewerToken);
        await Assert.That(found.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToList()).IsEquivalentTo([endUser.Id]);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{root}/users?q=no-such-address", viewerToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
        var live = await oidc.OkAsync(HttpMethod.Get, $"{root}/sessions?user={endUser.Id}", viewerToken);
        await Assert.That(live.GetProperty("items")[0].GetProperty("email").GetString()).IsEqualTo(endUser.Email);

        // The platform's own users are not the tenant's; a viewer cannot write; an outsider and an unknown project get 404.
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{root}/users/{owner.Id}", viewerToken)).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{root}/users/{endUser.Id}/disable", viewerToken)).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{root}/users", outsiderToken)).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"/v1/{AuthStore.NewId()}/users", ownerToken)).IsEqualTo((404, "not_found"));

        // Disabling ends the live session at once and refuses new ones; enabling lets the user sign in again.
        var disabled = await oidc.OkAsync(HttpMethod.Post, $"{root}/users/{endUser.Id}/disable", ownerToken);
        await Assert.That(disabled.GetProperty("disabled").GetBoolean()).IsTrue();
        await Assert.That(await sessions.ResolveAsync(project, session.Token)).IsNull();
        var current = (await users.GetAsync(project, endUser.Id))!;
        await Assert.That(async () => await sessions.CreateAsync(current)).Throws<ApiException>();
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{root}/sessions", viewerToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That((await oidc.OkAsync(HttpMethod.Post, $"{root}/users/{endUser.Id}/enable", ownerToken)).GetProperty("disabled").GetBoolean()).IsFalse();
        var renewed = await sessions.CreateAsync((await users.GetAsync(project, endUser.Id))!);

        // Session ids are canonical ULIDs; an admin revokes one by id and it stops resolving.
        var sessionId = (await oidc.OkAsync(HttpMethod.Get, $"{root}/sessions", viewerToken)).GetProperty("items")[0].GetProperty("id").GetString()!;
        await Assert.That(TerraScale.DotNet.TerraScaleIds.IsValid(sessionId)).IsTrue();
        using var revoked = await oidc.CallAsync(HttpMethod.Delete, $"{root}/sessions/{sessionId}", ownerToken);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await sessions.ResolveAsync(project, renewed.Token)).IsNull();
    }

    private string Email(int index) =>
        this.faker.Internet.Email(provider: "example.test", uniqueSuffix: index.ToString(CultureInfo.InvariantCulture)).ToLowerInvariant();

    // An owner signed in, with its sign-in token, an organization and a project.
    private async Task<(UserRecord Owner, string Token, string Project, string Root)> ProjectAsync(OidcHarness oidc)
    {
        var (owner, token) = await oidc.SignInUserAsync(this.Email(1));
        var organization = await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName());
        var project = await oidc.CreateProjectAsync(token, organization, this.faker.Commerce.ProductName());
        return (owner, token, project, $"/v1/{project}");
    }
}
