using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Identity;

public sealed class RegionalPasscodeWinnerTests
{
    private const string Instance = OidcIssuer.InstanceId;
    private const string Email = "regional-winner@example.test";

    [Test]
    public async Task Atomic_regional_resets_select_the_user_state_winner_even_when_the_redemption_claim_disagrees()
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        await using var gru = await OidcHarness.StartAsync(regions, "gru");
        await using var sjc = await OidcHarness.StartAsync(regions, "sjc");
        var nodes = new[] { gru, sjc };
        var stores = nodes.Select(Service<AuthStore>).ToArray();
        var original = await gru.CreateUserAsync(Email);
        var code = (await Service<Passcodes>(gru).RequestAsync(Instance, Email, PasscodePurpose.PasswordReset, CancellationToken.None))!;
        await ConvergedAsync(nodes);
        var passwords = new[] { "regional-gru-reset-password", "regional-sjc-reset-password" };
        var hashes = await Task.WhenAll(nodes.Select((node, index) => Service<PasswordHasher>(node).HashAsync(passwords[index], CancellationToken.None)));
        var resetting = new List<Task<UserRecord?>>();
        var signingIn = new List<Task<IssuedSession>>();
        var branches = new List<UserRecord>();
        var claimOwners = new List<string>();
        regions.HoldReplication();
        try
        {
            for (var index = 0; index < nodes.Length; index++)
            {
                var node = nodes[index];
                var hash = hashes[index];
                resetting.Add(Service<Passcodes>(node).ResetPasswordAsync(Instance, Email, code.Code, hash));
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    (await Service<Users>(node).GetAsync(Instance, original.Id))?.PasswordHash == hash,
                    TimeSpan.FromSeconds(10))).IsTrue();
                var user = (await Service<Users>(node).GetAsync(Instance, original.Id))!;
                var consumed = await PasscodeAsync(stores[index], original.Id);
                await Assert.That(consumed.Consumed).IsTrue();
                await Assert.That(user.AuthEpoch).IsEqualTo(original.AuthEpoch + 1);
                await Assert.That(user.AuthStamp).IsNotEqualTo(original.AuthStamp);
                branches.Add(user);
                claimOwners.Add((await stores[index].FindUniqueOwnerAsync(Instance, "credential:passcode",
                    AuthStore.NaturalKeyId(Instance, original.Id, consumed.IssueId)))!);

                // Staging: remote_durable holds the reset response; use its committed local user with the real session service.
                signingIn.Add(Service<Sessions>(node).CreateAsync(user));
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    (await node.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Count == 1,
                    TimeSpan.FromSeconds(10))).IsTrue();
            }

            await Assert.That(branches.Select(user => user.AuthStamp).Distinct().Count()).IsEqualTo(2);
        }
        finally
        {
            regions.ReleaseReplication();
            await Task.WhenAll(resetting);
            await Task.WhenAll(signingIn);
        }

        var results = await Task.WhenAll(resetting);
        await Assert.That(results.All(result => result is not null)).IsTrue();
        for (var index = 0; index < results.Length; index++)
        {
            await Assert.That(results[index]!.AuthStamp).IsEqualTo(branches[index].AuthStamp);
            await Assert.That(results[index]!.PasswordHash).IsEqualTo(hashes[index]);
        }

        await ConvergedAsync(nodes);
        var winner = (await Service<Users>(gru).GetAsync(Instance, original.Id))!;
        var winnerIndex = branches.FindIndex(user => user.AuthStamp == winner.AuthStamp);
        await Assert.That(winnerIndex >= 0).IsTrue();
        var loserIndex = 1 - winnerIndex;
        var sessions = await Task.WhenAll(signingIn);

        // Staging: give the losing redemption claim a later real write, independently of the already merged user state.
        var passcode = await PasscodeAsync(stores[0], original.Id);
        var claimKey = AuthStore.NaturalKeyId(Instance, original.Id, passcode.IssueId);
        var claim = (await stores[loserIndex].FindUniqueAsync(Instance, "credential:passcode", claimKey))!.Value;
        var losingClaimOwner = claimOwners[loserIndex];
        await Assert.That(losingClaimOwner).IsNotEqualTo(claimOwners[winnerIndex]);
        await Assert.That(await stores[loserIndex].TransactAsync([
            AuthStore.Reclaim(Instance, "credential:passcode", claimKey, losingClaimOwner, claim.Version),
        ])).IsNull();
        await ConvergedAsync(nodes);

        foreach (var node in nodes)
        {
            var current = (await Service<Users>(node).GetAsync(Instance, original.Id))!;
            await Assert.That(current.AuthStamp).IsEqualTo(winner.AuthStamp);
            await Assert.That(current.AuthEpoch).IsEqualTo(winner.AuthEpoch);
            await Assert.That(current.PasswordHash).IsEqualTo(hashes[winnerIndex]);
            await Assert.That(await Service<AuthStore>(node).FindUniqueOwnerAsync(Instance, "credential:passcode", claimKey)).IsEqualTo(losingClaimOwner);
            await Assert.That((await Service<HostedAuth>(node).CheckPasswordAsync(Email, passwords[winnerIndex], CancellationToken.None)).User?.AuthStamp).IsEqualTo(winner.AuthStamp);
            await Assert.That((await Service<HostedAuth>(node).CheckPasswordAsync(Email, passwords[loserIndex], CancellationToken.None)).User).IsNull();
            await Assert.That(await Service<Sessions>(node).ResolveAsync(Instance, sessions[winnerIndex].Token)).IsNotNull();
            await Assert.That(await Service<Sessions>(node).ResolveAsync(Instance, sessions[loserIndex].Token)).IsNull();
            await Assert.That(await Service<Users>(node).SetPasswordAsync(branches[loserIndex], hashes[loserIndex])).IsFalse();
            await RejectedWithNoticeAsync(node, sessions[loserIndex].Token);
        }

        // Same epoch is insufficient: a reset issued for a different stamp must leave both code and password untouched.
        var stale = (await Service<Passcodes>(gru).RequestAsync(Instance, Email, PasscodePurpose.PasswordReset, CancellationToken.None))!;
        var before = (await stores[0].GetAsync(AuthStore.UserCollection, Instance, original.Id))!;
        var advanced = winner with { AuthStamp = AuthStore.NewId() };
        await Assert.That(await stores[0].CompareAndPutAsync(AuthStore.UserCollection, Instance, original.Id,
            JsonSerializer.SerializeToElement(advanced, IdentityJson.Default.UserRecord), before.Version)).IsTrue();
        await ConvergedAsync(nodes);
        foreach (var node in nodes)
        {
            await Assert.That(await Service<Passcodes>(node).ResetPasswordAsync(Instance, Email, stale.Code, hashes[loserIndex])).IsNull();
            var current = (await Service<Users>(node).GetAsync(Instance, original.Id))!;
            await Assert.That(current.AuthEpoch).IsEqualTo(winner.AuthEpoch);
            await Assert.That(current.AuthStamp).IsEqualTo(advanced.AuthStamp);
            await Assert.That(current.PasswordHash).IsEqualTo(winner.PasswordHash);
            await Assert.That((await PasscodeAsync(Service<AuthStore>(node), original.Id)).Consumed).IsFalse();
        }
    }

    [Test]
    public async Task Distinct_regional_passcode_issuances_redeemed_before_healing_leave_one_session_winner_everywhere()
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        await using var gru = await OidcHarness.StartAsync(regions, "gru");
        await using var sjc = await OidcHarness.StartAsync(regions, "sjc");
        var nodes = new[] { gru, sjc };
        var user = await gru.CreateUserAsync(Email);
        await Service<Passcodes>(gru).RequestAsync(Instance, Email);
        await ConvergedAsync(nodes);
        var stores = nodes.Select(Service<AuthStore>).ToArray();
        var predecessor = await PasscodeAsync(stores[0], user.Id);
        var slot = AuthStore.NaturalKeyId(Instance, user.Id, AuthStore.NaturalKeyId(Instance, user.AuthStamp!, predecessor.IssueId));
        var codes = new[] { "123456", "654321" };
        var issuance = codes.Select(_ => AuthStore.NewCredentialBinding("passcode-issuance", slot)).ToArray();
        var issued = nodes.Select((node, index) => predecessor with
        {
            CodeHash = Digest(node, user.Id, codes[index]),
            IssueId = issuance[index].OwnerId,
            IssuanceBindings = [.. predecessor.IssuanceBindings, issuance[index]],
            IssuedInWindow = predecessor.IssuedInWindow + 1,
        }).ToArray();
        var staging = new Task<int?>?[nodes.Length];
        var redeeming = new Task<UserRecord?>?[nodes.Length];
        var signingIn = new Task<IssuedSession>?[nodes.Length];
        regions.HoldReplication();
        try
        {
            await Task.WhenAll(nodes.Select(async (node, index) =>
            {
                var store = stores[index];
                var candidate = issued[index];
                var document = (await store.GetAsync(AuthStore.PasscodeCollection, Instance, user.Id))!;
                // Staging: issuance responses cannot finish while held. Write the exact issuance/predecessor shape locally through the SDK transaction.
                staging[index] = store.TransactAsync([
                    AuthStore.Replace(AuthStore.PasscodeCollection, Instance, user.Id,
                        JsonSerializer.SerializeToElement(candidate, IdentityJson.Default.PasscodeRecord), document.Version),
                    AuthStore.CredentialClaim(Instance, issuance[index]),
                ]);
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    (await PasscodeAsync(store, user.Id)).IssueId == candidate.IssueId,
                    TimeSpan.FromSeconds(10))).IsTrue();
                redeeming[index] = Service<Passcodes>(node).RedeemAsync(Instance, Email, codes[index], PasscodePurpose.SignIn);
                var redemptionKey = AuthStore.NaturalKeyId(Instance, user.Id, candidate.IssueId);
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    await store.FindUniqueOwnerAsync(Instance, "credential:passcode", redemptionKey) is not null,
                    TimeSpan.FromSeconds(10))).IsTrue();
                await Assert.That((await PasscodeAsync(store, user.Id)).Consumed).IsTrue();
                var owner = (await store.FindUniqueOwnerAsync(Instance, "credential:passcode", redemptionKey))!;
                // Staging: resume at the committed redemption boundary using production session checks and persistence.
                signingIn[index] = Service<Sessions>(node).CreateAsync(user with
                {
                    AuthenticationBindings = [.. user.AuthenticationBindings, .. candidate.IssuanceBindings, new CredentialBinding("credential:passcode", redemptionKey, owner)],
                });
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    (await node.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Count == 1,
                    TimeSpan.FromSeconds(10))).IsTrue();
            }));
        }
        finally
        {
            regions.ReleaseReplication();
            // Observe only started writes; cleanup must preserve a staging failure as the original exception.
            await Task.WhenAll(staging.OfType<Task>().Concat(redeeming.OfType<Task>()).Concat(signingIn.OfType<Task>()))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await Assert.That((await Task.WhenAll(staging.Select(task => task!))).All(result => result is null)).IsTrue();
        await Assert.That((await Task.WhenAll(redeeming.Select(task => task!))).All(result => result is not null)).IsTrue();
        var sessions = await Task.WhenAll(signingIn.Select(task => task!));
        await ConvergedAsync(nodes);
        var winningIssue = await stores[0].FindUniqueOwnerAsync(Instance, "credential:passcode-issuance", slot);
        await Assert.That(issuance.Count(binding => binding.OwnerId == winningIssue)).IsEqualTo(1);
        for (var index = 0; index < sessions.Length; index++)
        {
            var wins = issuance[index].OwnerId == winningIssue;
            foreach (var node in nodes)
            {
                await Assert.That(await Service<AuthStore>(node).FindUniqueOwnerAsync(Instance, "credential:passcode-issuance", slot)).IsEqualTo(winningIssue);
                await Assert.That(await Service<Sessions>(node).ResolveAsync(Instance, sessions[index].Token) is not null).IsEqualTo(wins);
                if (!wins)
                {
                    await RejectedWithNoticeAsync(node, sessions[index].Token);
                }
            }
        }

        // A rejected branch must not revoke the other branch as a side effect of the hosted authorization check.
        var winningSession = sessions[Array.FindIndex(issuance, binding => binding.OwnerId == winningIssue)];
        foreach (var node in nodes)
        {
            await Assert.That(await Service<Sessions>(node).ResolveAsync(Instance, winningSession.Token)).IsNotNull();
        }
    }

    private static T Service<T>(OidcHarness node) where T : notnull => node.Server.App.Services.GetRequiredService<T>();

    private static async Task<PasscodeRecord> PasscodeAsync(AuthStore store, string userId) =>
        (await store.GetAsync(AuthStore.PasscodeCollection, Instance, userId))!.Document.Deserialize(IdentityJson.Default.PasscodeRecord)!;

    private static string Digest(OidcHarness node, string userId, string code)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Service<AuthOptions>(node).MasterKey, 32, info: "ts-auth passcode digests"u8.ToArray());
        return Convert.ToHexStringLower(HMACSHA256.HashData(key,
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Instance.Length}:{Instance}{userId.Length}:{userId}{code}"))));
    }

    private static async Task ConvergedAsync(OidcHarness[] nodes)
    {
        foreach (var collection in new[] { AuthStore.UserCollection, AuthStore.PasscodeCollection, AuthStore.UniqueCollection, AuthStore.SessionCollection })
        {
            await Assert.That(await ReadinessWait.UntilAsync(async () =>
            {
                var copies = await Task.WhenAll(nodes.Select(node => node.Database.ListDocumentsAsync(collection)));
                return copies[0].Items.Select(document => document.Id + ":" + document.Version).Order(StringComparer.Ordinal)
                    .SequenceEqual(copies[1].Items.Select(document => document.Id + ":" + document.Version).Order(StringComparer.Ordinal));
            }, TimeSpan.FromSeconds(15))).IsTrue();
        }
    }

    private static async Task RejectedWithNoticeAsync(OidcHarness node, string token)
    {
        using var rejected = await node.AuthorizeAsync(OidcHarness.AuthorizePath(OidcHarness.Pkce().Challenge), token);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var location = rejected.Headers.Location!.OriginalString;
        await Assert.That(location).Contains("notice=credential_changed");
        using var page = await node.Client.GetAsync(location);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await page.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("another region");
        await Assert.That(body).Contains("Sign in again");
    }
}
