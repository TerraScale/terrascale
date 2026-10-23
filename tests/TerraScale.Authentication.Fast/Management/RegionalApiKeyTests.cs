using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

public sealed class RegionalApiKeyTests
{
    private static readonly string[] ReadOnly = ["documents:read"];
    private static readonly string[] Collections = [AuthStore.ApiKeyCollection, AuthStore.MembershipCollection, AuthStore.UserCollection];

    [Test]
    public async Task Keys_outlive_their_creators_sign_in_across_regions_and_stop_everywhere_once_revoked()
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        await using var gru = await OidcHarness.StartAsync(regions, "gru");
        await using var sjc = await OidcHarness.StartAsync(regions, "sjc");
        var nodes = new[] { gru, sjc };
        var (user, _, initial) = await gru.SignInAndRedeemAsync();
        var token = initial.GetProperty("access_token").GetString()!;
        var organization = await gru.CreateOrganizationAsync(token);
        var path = $"{Organizations}/{organization}/api-keys";

        // One key written in each region.
        var kept = (await gru.OkAsync(HttpMethod.Post, path, token, new { name = "kept", scopes = ReadOnly }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        await Assert.That(await ConvergedAsync(nodes)).IsTrue();
        var revoked = (await sjc.OkAsync(HttpMethod.Post, path, token, new { name = "revoked", scopes = ReadOnly }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        await Assert.That(await ConvergedAsync(nodes)).IsTrue();
        foreach (var node in nodes)
        {
            await AssertExchangeAsync(node, kept, succeeds: true);
            await AssertExchangeAsync(node, revoked, succeeds: true);
        }

        // Revoking in one region stops the key in every region once the deletion replicates.
        await sjc.OkAsync(HttpMethod.Delete, $"{path}/{revoked[..26]}", token, status: HttpStatusCode.NoContent);
        await Assert.That(await ConvergedAsync(nodes)).IsTrue();
        foreach (var node in nodes)
        {
            await AssertExchangeAsync(node, revoked, succeeds: false);
        }

        // Signing out everywhere advances the creator's authentication-state winner in both regions; the key it created is
        // the organization's credential, not the sign-in's, so it keeps working.
        await Assert.That(await gru.Server.App.Services.GetRequiredService<Sessions>().RevokeAllAsync(OidcIssuer.InstanceId, user.Id)).IsTrue();
        await Assert.That(await ConvergedAsync(nodes)).IsTrue();
        foreach (var node in nodes)
        {
            await AssertExchangeAsync(node, kept, succeeds: true);
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
