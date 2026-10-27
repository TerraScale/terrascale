using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class CredentialReadTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments(0)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(15)]
    public async Task Every_ancestor_stays_fresh_after_warming_both_auth_paths(int losingIndex)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var ancestors = Enumerable.Range(0, 16).Select(_ => AuthStore.NewCredentialBinding("oidc", AuthStore.NewId())).ToArray();
        await Assert.That(await store.TransactAsync(ancestors.Select(binding => AuthStore.CredentialClaim(OidcIssuer.InstanceId, binding)).ToArray())).IsNull();

        // Model a long refresh lineage in the persisted entries; all requests still validate real signed tokens.
        var entries = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items;
        foreach (var entry in entries)
        {
            var document = JsonNode.Parse(entry.Document.GetRawText())!;
            var bindings = document["bindings"]!.AsArray();
            foreach (var binding in ancestors.Append(ancestors[0]))
            {
                bindings.Add(new JsonObject { ["kind"] = binding.Kind, ["key"] = binding.Key, ["ownerId"] = binding.OwnerId });
            }

            await Assert.That(await store.CompareAndPutAsync(AuthStore.OidcTokenCollection, OidcIssuer.InstanceId,
                document["id"]!.GetValue<string>(), JsonSerializer.SerializeToElement(document), entry.Version)).IsTrue();
        }

        var access = tokens.GetProperty("access_token").GetString()!;
        using var warmInfo = await oidc.UserInfoAsync(access);
        using var warmOrganizations = await oidc.CallAsync(HttpMethod.Get, PlatformCalls.Organizations, access);
        await Assert.That(warmInfo.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(warmOrganizations.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var loser = ancestors[losingIndex];
        var held = (await store.FindUniqueAsync(OidcIssuer.InstanceId, loser.Kind, loser.Key))!.Value;
        await Assert.That(await store.TransactAsync([
            AuthStore.Reclaim(OidcIssuer.InstanceId, loser.Kind, loser.Key, AuthStore.NewId(), held.Version),
        ])).IsNull();

        using var info = await oidc.UserInfoAsync(access);
        using var organizations = await oidc.CallAsync(HttpMethod.Get, PlatformCalls.Organizations, access);
        using var refresh = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(info.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(organizations.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(refresh)).IsEqualTo("invalid_grant");
    }
}
