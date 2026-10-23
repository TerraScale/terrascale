using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class EndUserAccessTokenTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_live_tenant_session_issues_one_project_with_stored_roles_and_no_administrator_scopes()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, owner) = await oidc.SignInUserAsync("owner@example.test");
        var project = await oidc.CreateProjectAsync(owner, await oidc.CreateOrganizationAsync(owner), "Production");
        var services = oidc.Server.App.Services;
        var users = services.GetRequiredService<Users>();
        var sessions = services.GetRequiredService<Sessions>();
        var user = (await users.CreateAsync(project, "customer@example.test"))!;
        var id = AuthStore.NaturalKeyId(project, AuthStore.UserCollection, user.Id);
        var stored = (await oidc.Database.GetDocumentAsync(AuthStore.UserCollection, id))!;
        var document = JsonNode.Parse(stored.Document.GetRawText())!.AsObject();
        document["roles"] = new JsonArray("reader", "Reader", "reader");
        document["platformRole"] = "admin";
        await oidc.Database.PutDocumentAsync(AuthStore.UserCollection, id, JsonSerializer.SerializeToElement(document), TerraBasePrecondition.Matches(stored.Version));
        var session = await sessions.CreateAsync((await users.GetAsync(project, user.Id))!);
        var issuer = ActivatorUtilities.CreateInstance<EndUserAccessTokens>(services);
        var encoded = await issuer.IssueAsync(project, session.Token);
        await Assert.That(encoded).IsNotNull();

        var keys = new JsonWebKeySet(await oidc.Client.GetStringAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json"));
        var checkedToken = await new JsonWebTokenHandler().ValidateTokenAsync(encoded!, new TokenValidationParameters
        {
            ValidIssuer = OidcHarness.Issuer,
            ValidAudiences = services.GetRequiredService<AuthOptions>().AccessTokenAudiences,
            IssuerSigningKeys = keys.GetSigningKeys(),
            TryAllIssuerSigningKeys = false,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidTypes = ["at+jwt"],
            ClockSkew = TimeSpan.Zero,
        });
        await Assert.That(checkedToken.IsValid).IsTrue();
        var jwt = (JsonWebToken)checkedToken.SecurityToken;
        await Assert.That(jwt.Subject).IsEqualTo(user.Id);
        await Assert.That(jwt.GetPayloadValue<string>("project_id")).IsEqualTo(project);
        await Assert.That(jwt.GetPayloadValue<string>("instance_id")).IsEqualTo(project);
        await Assert.That(jwt.GetPayloadValue<string>("token_kind")).IsEqualTo("end_user");
        await Assert.That(jwt.GetPayloadValue<string[]>("roles")).IsEquivalentTo(["reader", "Reader"]);
        foreach (var claim in new[] { "scope", "database", "platform_role", "org_id" })
        {
            await Assert.That(jwt.TryGetPayloadValue<object>(claim, out _)).IsFalse().Because(claim);
        }

        await Assert.That(jwt.ValidTo - jwt.IssuedAt).IsEqualTo(EndUserAccessTokens.Lifetime);
        await Assert.That(jwt.ValidTo <= session.Session.ExpiresAt.UtcDateTime).IsTrue();
        await sessions.RevokeAsync(project, session.Token);
        await Assert.That(await issuer.IssueAsync(project, session.Token)).IsNull();
    }

    [Test]
    public async Task Platform_foreign_tenant_revoked_and_deleted_project_sessions_cannot_mint()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, owner) = await oidc.SignInUserAsync("owner@example.test");
        var organization = await oidc.CreateOrganizationAsync(owner);
        var project = await oidc.CreateProjectAsync(owner, organization, "Production");
        var other = await oidc.CreateProjectAsync(owner, organization, "Other");
        var services = oidc.Server.App.Services;
        var users = services.GetRequiredService<Users>();
        var sessions = services.GetRequiredService<Sessions>();
        var issuer = ActivatorUtilities.CreateInstance<EndUserAccessTokens>(services);
        var platform = await sessions.CreateAsync((await users.FindByEmailAsync(OidcIssuer.InstanceId, "owner@example.test"))!);
        var foreign = await sessions.CreateAsync((await users.CreateAsync(other, "customer@example.test"))!);
        await Assert.That(await issuer.IssueAsync(project, platform.Token)).IsNull();
        await Assert.That(await issuer.IssueAsync(project, foreign.Token)).IsNull();
        await Assert.That(await issuer.IssueAsync(project, null)).IsNull();
        var local = await sessions.CreateAsync((await users.CreateAsync(project, "customer@example.test"))!);
        await sessions.RevokeAllAsync(project, local.Session.UserId);
        await Assert.That(await issuer.IssueAsync(project, local.Token)).IsNull();
        var fresh = await sessions.CreateAsync((await users.GetAsync(project, local.Session.UserId))!);
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{project}", owner, status: System.Net.HttpStatusCode.NoContent);
        await Assert.That(await issuer.IssueAsync(project, fresh.Token)).IsNull();
    }

    [Test]
    public async Task Token_expiry_is_capped_at_the_live_sessions_absolute_expiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (_, owner) = await oidc.SignInUserAsync("owner@example.test");
        var project = await oidc.CreateProjectAsync(owner, await oidc.CreateOrganizationAsync(owner), "Production");
        var services = oidc.Server.App.Services;
        var user = (await services.GetRequiredService<Users>().CreateAsync(project, "customer@example.test"))!;
        var session = await services.GetRequiredService<Sessions>().CreateAsync(user);
        var issuer = ActivatorUtilities.CreateInstance<EndUserAccessTokens>(services);
        clock.Advance(Sessions.Lifetime - TimeSpan.FromMinutes(1));
        var encoded = await issuer.IssueAsync(project, session.Token);
        await Assert.That(encoded).IsNotNull();
        var jwt = new JsonWebToken(encoded!);
        await Assert.That(jwt.ValidTo - jwt.IssuedAt).IsEqualTo(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await issuer.IssueAsync(project, session.Token)).IsNull();
    }
}
