using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

public sealed class RegionalApiKeyLineageTests
{
    private static readonly string[] ReadOnly = ["documents:read"];
    private static readonly string[] Collections = [AuthStore.OidcTokenCollection, AuthStore.UniqueCollection, AuthStore.ApiKeyCollection, AuthStore.MembershipCollection, AuthStore.UserCollection];

    [Test]
    public async Task Losing_sign_in_cannot_escape_through_created_or_rotated_keys_and_grace_keeps_each_secrets_lineage()
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        await using var gru = await OidcHarness.StartAsync(regions, "gru");
        await using var sjc = await OidcHarness.StartAsync(regions, "sjc");
        var nodes = new[] { gru, sjc };
        var stores = nodes.Select(node => node.Server.App.Services.GetRequiredService<AuthStore>()).ToArray();
        var (user, session, initial) = await gru.SignInAndRedeemAsync();
        var stable = initial.GetProperty("access_token").GetString()!;
        var organization = await gru.CreateOrganizationAsync(stable);
        var path = $"{Organizations}/{organization}/api-keys";
        var tokens = new List<string>();
        var originals = new List<string>();
        foreach (var region in RegionalTerraBaseFixture.Regions)
        {
            var (verifier, challenge) = OidcHarness.Pkce();
            using var response = await gru.RedeemCodeAsync(await gru.CodeAsync(session, challenge), verifier);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            tokens.Add((await OidcHarness.JsonAsync(response)).GetProperty("access_token").GetString()!);
            originals.Add((await gru.OkAsync(HttpMethod.Post, path, stable,
                new { name = "rotate-" + region, scopes = ReadOnly }, HttpStatusCode.Created)).GetProperty("key").GetString()!);
        }

        await Assert.That(await ConvergedAsync(nodes)).IsTrue();
        var claimKey = AuthStore.NewId();
        var bindings = nodes.Select(_ => AuthStore.NewCredentialBinding("oidc", claimKey)).ToArray();
        var staging = new List<Task<int?>>();
        var creating = new List<Task<JsonElement>>();
        var rotating = new List<Task<JsonElement>>();
        regions.HoldReplication();
        try
        {
            // Real JWTs and HTTP handlers; only the competing sign-in ancestry is staged. Remote-durable replies
            // cannot finish under a partition, so observe local key commits before releasing the real peer links.
            for (var index = 0; index < nodes.Length; index++)
            {
                var store = stores[index];
                var binding = bindings[index];
                var tokenId = new JsonWebToken(tokens[index]).GetClaim("oi_tkn_id").Value;
                var document = (await store.GetAsync(AuthStore.OidcTokenCollection, OidcIssuer.InstanceId, tokenId))!;
                var token = JsonNode.Parse(document.Document.GetRawText())!.AsObject();
                token["bindings"]!.AsArray().Add(new JsonObject { ["kind"] = binding.Kind, ["key"] = binding.Key, ["ownerId"] = binding.OwnerId });
                staging.Add(store.TransactAsync(
                [
                    AuthStore.CredentialClaim(OidcIssuer.InstanceId, binding),
                    AuthStore.Replace(AuthStore.OidcTokenCollection, OidcIssuer.InstanceId, tokenId, JsonSerializer.SerializeToElement(token), document.Version),
                ]));
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    await store.FindUniqueOwnerAsync(OidcIssuer.InstanceId, binding.Kind, binding.Key) == binding.OwnerId
                    && (await store.GetAsync(AuthStore.OidcTokenCollection, OidcIssuer.InstanceId, tokenId))!.Document
                        .GetProperty("bindings").EnumerateArray().Any(item => item.GetProperty("ownerId").GetString() == binding.OwnerId),
                    TimeSpan.FromSeconds(5))).IsTrue();

                creating.Add(nodes[index].OkAsync(HttpMethod.Post, path, tokens[index],
                    new { name = "created-" + index, scopes = ReadOnly }, HttpStatusCode.Created));
                rotating.Add(nodes[index].OkAsync(HttpMethod.Post, $"{path}/{originals[index][..26]}/rotate", tokens[index],
                    new { previous_secret_ttl = 86400 }));
            }

            for (var index = 0; index < nodes.Length; index++)
            {
                var node = nodes[index];
                var binding = bindings[index];
                var originalId = originals[index][..26];
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                {
                    var keys = await node.Database.ListDocumentsAsync(AuthStore.ApiKeyCollection);
                    return keys.Items.Count(document => document.Document.GetProperty("secretBindings").EnumerateArray()
                        .Any(item => item.GetProperty("ownerId").GetString() == binding.OwnerId)) == 2
                        && keys.Items.Any(document => document.Document.GetProperty("id").GetString() == originalId
                            && document.Document.GetProperty("previousSecretHash").ValueKind == JsonValueKind.String);
                }, TimeSpan.FromSeconds(5))).IsTrue();
            }
        }
        finally
        {
            regions.ReleaseReplication();
        }

        foreach (var operation in staging)
        {
            await Assert.That(await operation).IsNull();
        }

        var created = (await Task.WhenAll(creating)).Select(body => body.GetProperty("key").GetString()!).ToArray();
        var rotated = (await Task.WhenAll(rotating)).Select(body => body.GetProperty("key").GetString()!).ToArray();
        await Assert.That(await ConvergedAsync(nodes)).IsTrue();
        var winner = await stores[0].FindUniqueOwnerAsync(OidcIssuer.InstanceId, bindings[0].Kind, claimKey);
        await Assert.That(bindings.Count(binding => binding.OwnerId == winner)).IsEqualTo(1);
        var replacements = new List<string>();
        for (var index = 0; index < nodes.Length; index++)
        {
            var wins = bindings[index].OwnerId == winner;
            foreach (var node in nodes)
            {
                await AssertExchangeAsync(node, created[index], wins);
                await AssertExchangeAsync(node, rotated[index], wins);
                // A losing rotation cannot invalidate the previous secret's independent winning sign-in ancestry.
                await AssertExchangeAsync(node, originals[index], succeeds: true);
            }

            // The converse: a fresh valid sign-in rotates the created key; its losing previous secret remains refused
            // during grace while the new secret works. A winning previous secret still receives its existing grace.
            var replacement = await gru.OkAsync(HttpMethod.Post, $"{path}/{created[index][..26]}/rotate", stable,
                new { previous_secret_ttl = 86400 });
            var secret = replacement.GetProperty("key").GetString()!;
            replacements.Add(secret);
            await Assert.That(await ConvergedAsync(nodes)).IsTrue();
            foreach (var node in nodes)
            {
                await AssertExchangeAsync(node, secret, succeeds: true);
                await AssertExchangeAsync(node, created[index], wins);
            }
        }

        // Historical records cannot prove either secret's ancestry; absent fields must not become empty safe chains.
        var oldKey = (await stores[0].GetAsync(AuthStore.ApiKeyCollection, OidcIssuer.InstanceId, created[0][..26]))!;
        var oldRecord = JsonNode.Parse(oldKey.Document.GetRawText())!.AsObject();
        oldRecord.Remove("secretBindings");
        oldRecord.Remove("previousSecretBindings");
        await Assert.That(await stores[0].ReplaceAsync(AuthStore.ApiKeyCollection, OidcIssuer.InstanceId, created[0][..26],
            JsonSerializer.SerializeToElement(oldRecord), oldKey.Version)).IsNotNull();
        await Assert.That(await ConvergedAsync(nodes)).IsTrue();
        foreach (var node in nodes)
        {
            await AssertExchangeAsync(node, replacements[0], succeeds: false);
            await AssertExchangeAsync(node, created[0], succeeds: false);
        }

        // Every retained secret also depends on its issuing user's authentication-state winner.
        await Assert.That(await gru.Server.App.Services.GetRequiredService<Sessions>().RevokeAllAsync(OidcIssuer.InstanceId, user.Id)).IsTrue();
        await Assert.That(await ConvergedAsync(nodes)).IsTrue();
        foreach (var node in nodes)
        {
            await AssertExchangeAsync(node, replacements[1], succeeds: false);
            await AssertExchangeAsync(node, originals[1], succeeds: false);
        }
    }

    private static async Task AssertExchangeAsync(OidcHarness node, string secret, bool succeeds)
    {
        using var response = await node.ApiKeyTokenAsync(secret);
        var body = await OidcHarness.JsonAsync(response);
        await Assert.That(response.StatusCode).IsEqualTo(succeeds ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
        await Assert.That(body.TryGetProperty("access_token", out _)).IsEqualTo(succeeds);
        if (!succeeds)
        {
            await Assert.That(body.GetProperty("error").GetString()).IsEqualTo("invalid_client");
            var description = body.GetProperty("error_description").GetString()!;
            await Assert.That(description).Contains("concurrent");
            await Assert.That(description).Contains("region");
            await Assert.That(description).Contains("Use the latest credential");
        }
    }

    private static Task<bool> ConvergedAsync(OidcHarness[] nodes) => ReadinessWait.UntilAsync(async () =>
    {
        foreach (var collection in Collections)
        {
            var copies = await Task.WhenAll(nodes.Select(node => node.Database.ListDocumentsAsync(collection)));
            if (!copies[0].Items.Select(document => document.Version).Order(StringComparer.Ordinal)
                .SequenceEqual(copies[1].Items.Select(document => document.Version).Order(StringComparer.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }, TimeSpan.FromSeconds(10));
}
