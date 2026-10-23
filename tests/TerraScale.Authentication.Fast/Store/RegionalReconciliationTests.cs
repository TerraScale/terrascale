using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Store;

public sealed class RegionalReconciliationTests
{
    [Test]
    public async Task Concurrent_email_claims_converge_and_the_losing_identity_is_rejected_in_both_regions()
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        await using var gru = await regions.StartAuthAsync("gru");
        await using var sjc = await regions.StartAuthAsync("sjc");
        var auth = new[] { gru, sjc };
        var stores = auth.Select(server => server.App.Services.GetRequiredService<AuthStore>()).ToArray();
        var users = auth.Select(server => server.App.Services.GetRequiredService<Users>()).ToArray();
        var email = "concurrent@example.test";
        regions.HoldReplication();
        var creating = users.Select(user => user.CreateAsync(OidcIssuer.InstanceId, email)).ToArray();
        try
        {
            for (var index = 0; index < stores.Length; index++)
            {
                var store = stores[index];
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    await store.FindUniqueOwnerAsync(OidcIssuer.InstanceId, "email", email) is not null,
                    TimeSpan.FromSeconds(5))).IsTrue();
            }
        }
        finally
        {
            regions.ReleaseReplication();
        }

        var contenders = await Task.WhenAll(creating);
        await Assert.That(contenders.All(user => user is not null)).IsTrue();
        await Assert.That(contenders.Select(user => user!.Id).Distinct().Count()).IsEqualTo(2);
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
        {
            var owners = await Task.WhenAll(stores.Select(store => store.FindUniqueOwnerAsync(OidcIssuer.InstanceId, "email", email)));
            return owners[0] is not null && owners.Distinct().Count() == 1;
        }, TimeSpan.FromSeconds(10))).IsTrue();
        var winner = await stores[0].FindUniqueOwnerAsync(OidcIssuer.InstanceId, "email", email);
        foreach (var regionalUsers in users)
        {
            await Assert.That((await regionalUsers.FindByEmailAsync(OidcIssuer.InstanceId, email))?.Id).IsEqualTo(winner);
            foreach (var contender in contenders)
            {
                await Assert.That(await regionalUsers.GetAsync(OidcIssuer.InstanceId, contender!.Id) is not null).IsEqualTo(contender.Id == winner);
            }
        }
    }

    [Test]
    public async Task Concurrent_regional_session_rotations_leave_one_usable_successor_everywhere()
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        await using var gru = await regions.StartAuthAsync("gru");
        await using var sjc = await regions.StartAuthAsync("sjc");
        var nodes = new[] { gru, sjc };
        var stores = nodes.Select(node => node.App.Services.GetRequiredService<AuthStore>()).ToArray();
        var sessions = nodes.Select(node => node.App.Services.GetRequiredService<Sessions>()).ToArray();
        var user = (await gru.App.Services.GetRequiredService<Users>().CreateAsync(OidcIssuer.InstanceId, "rotation@example.test"))!;
        var original = await sessions[0].CreateAsync(user);
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
            await sessions[1].ResolveAsync(user.InstanceId, original.Token) is not null, TimeSpan.FromSeconds(10))).IsTrue();
        regions.HoldReplication();
        var rotating = sessions.Select(service => service.RotateAsync(user.InstanceId, original.Token)).ToArray();
        try
        {
            await Assert.That(await ReadinessWait.UntilAsync(async () =>
                (await Task.WhenAll(stores.Select(store => store.FindUniqueOwnerAsync(user.InstanceId, "credential:session", original.Session.TokenHash))))
                    .All(owner => owner is not null), TimeSpan.FromSeconds(5))).IsTrue();
        }
        finally
        {
            regions.ReleaseReplication();
        }

        var successors = (await Task.WhenAll(rotating)).OfType<IssuedSession>().ToArray();
        await Assert.That(successors.Length > 0).IsTrue();
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
        {
            var owners = await Task.WhenAll(stores.Select(store => store.FindUniqueOwnerAsync(user.InstanceId, "credential:session", original.Session.TokenHash)));
            var copies = await Task.WhenAll(RegionalTerraBaseFixture.Regions.Select(region => regions.Database(region).ListDocumentsAsync(AuthStore.SessionCollection)));
            return owners[0] is not null && owners.Distinct().Count() == 1
                && copies[0].Items.Select(item => item.Version).Order(StringComparer.Ordinal)
                    .SequenceEqual(copies[1].Items.Select(item => item.Version).Order(StringComparer.Ordinal));
        }, TimeSpan.FromSeconds(10))).IsTrue();
        string? winningToken = null;
        foreach (var service in sessions)
        {
            var live = new List<string>();
            foreach (var successor in successors)
            {
                if (await service.ResolveAsync(user.InstanceId, successor.Token) is not null)
                {
                    live.Add(successor.Token);
                }
            }

            await Assert.That(live.Count).IsEqualTo(1);
            winningToken ??= live[0];
            await Assert.That(live[0] == winningToken).IsTrue();
            await Assert.That(await service.ResolveAsync(user.InstanceId, original.Token)).IsNull();
            await Assert.That(await service.RotateAsync(user.InstanceId, original.Token)).IsNull();
            await Assert.That(await service.ResolveAsync(user.InstanceId, winningToken)).IsNotNull();
        }
    }

    [Test]
    [Arguments("authorization_code")]
    [Arguments("refresh_token")]
    public async Task Concurrent_redemptions_converge_and_only_the_winning_store_bound_tokens_work_everywhere(string grant)
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        var admissions = new[] { new TokenAdmissionBarrier(), new TokenAdmissionBarrier() };
        await using var gru = await OidcHarness.StartAsync(regions, "gru", app => app.Use(admissions[0].InvokeAsync));
        await using var sjc = await OidcHarness.StartAsync(regions, "sjc", app => app.Use(admissions[1].InvokeAsync));
        var auth = new[] { gru, sjc };
        var stores = auth.Select(server => server.Server.App.Services.GetRequiredService<AuthStore>()).ToArray();
        await gru.CreateUserAsync();
        var session = await gru.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        var credential = await gru.CodeAsync(session, challenge);
        if (grant == "refresh_token")
        {
            using var initial = await gru.RedeemCodeAsync(credential, verifier);
            await Assert.That(initial.StatusCode).IsEqualTo(HttpStatusCode.OK);
            credential = (await OidcHarness.JsonAsync(initial)).GetProperty("refresh_token").GetString()!;
        }

        var tokenType = grant == "authorization_code" ? "urn:openiddict:params:oauth:token-type:authorization_code" : "urn:ietf:params:oauth:token-type:refresh_token";
        var parent = (await gru.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items.Single(document =>
            document.Document.GetProperty("type").GetString() == tokenType && document.Document.GetProperty("status").GetString() == "valid");
        var parentId = parent.Document.GetProperty("id").GetString()!;
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
            (await sjc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items.Any(document => document.Version == parent.Version),
            TimeSpan.FromSeconds(10))).IsTrue();

        foreach (var admission in admissions)
        {
            admission.Arm();
        }

        var redeeming = auth.Select(node => grant == "authorization_code"
            ? node.RedeemCodeAsync(credential, verifier) : node.RefreshAsync(credential)).ToArray();
        try
        {
            // Real HTTP rate-limit writes finish before the partition; no admission checks are bypassed.
            await Task.WhenAll(admissions.Select(admission => admission.Reached)).WaitAsync(TimeSpan.FromSeconds(15));
            regions.HoldReplication();
            foreach (var admission in admissions)
            {
                admission.Release();
            }

            foreach (var store in stores)
            {
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    await store.FindUniqueOwnerAsync(OidcIssuer.InstanceId, "credential:oidc", parentId) is not null,
                    TimeSpan.FromSeconds(5))).IsTrue();
            }
        }
        finally
        {
            foreach (var admission in admissions)
            {
                admission.Release();
            }

            regions.ReleaseReplication();
        }

        var tokens = new List<JsonElement>();
        foreach (var redemption in redeeming)
        {
            using var response = await redemption;
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            tokens.Add(await OidcHarness.JsonAsync(response));
        }

        foreach (var contender in tokens)
        {
            var access = await gru.ValidateAsync(contender.GetProperty("access_token").GetString()!, OidcHarness.Issuer);
            await Assert.That(access.ValidTo > access.IssuedAt).IsTrue();
            await Assert.That(access.ValidTo - access.IssuedAt <= TimeSpan.FromMinutes(5)).IsTrue();
        }

        await Assert.That(await ReadinessWait.UntilAsync(async () =>
        {
            var owners = await Task.WhenAll(stores.Select(store => store.FindUniqueOwnerAsync(OidcIssuer.InstanceId, "credential:oidc", parentId)));
            var copies = await Task.WhenAll(auth.Select(node => node.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)));
            return owners[0] is not null && owners.Distinct().Count() == 1
                && copies[0].Items.Select(document => document.Version).Order(StringComparer.Ordinal)
                    .SequenceEqual(copies[1].Items.Select(document => document.Version).Order(StringComparer.Ordinal));
        }, TimeSpan.FromSeconds(10))).IsTrue();

        // Userinfo validates the stored token entry; this makes no claim about independent offline JWT consumers.
        var outcomes = new List<bool>();
        foreach (var contender in tokens)
        {
            var statuses = new List<HttpStatusCode>();
            foreach (var node in auth)
            {
                using var response = await node.UserInfoAsync(contender.GetProperty("access_token").GetString()!);
                statuses.Add(response.StatusCode);
            }

            await Assert.That(statuses.Distinct().Count()).IsEqualTo(1);
            await Assert.That(statuses[0] is HttpStatusCode.OK or HttpStatusCode.Unauthorized).IsTrue();
            outcomes.Add(statuses[0] == HttpStatusCode.OK);
        }

        await Assert.That(outcomes.Count(valid => valid)).IsEqualTo(1);
        var losingTokens = tokens[outcomes.IndexOf(false)];
        // Offline consumers accept the signed loser until exp; store-bound refresh already rejects it.
        foreach (var node in auth)
        {
            var offline = await node.ValidateAsync(losingTokens.GetProperty("access_token").GetString()!, OidcHarness.Issuer);
            await Assert.That(offline.ValidTo > DateTime.UtcNow).IsTrue();
        }

        var loser = losingTokens.GetProperty("refresh_token").GetString()!;
        foreach (var node in auth)
        {
            using var rejected = await node.RefreshAsync(loser);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            var error = await OidcHarness.JsonAsync(rejected);
            await Assert.That(error.GetProperty("error").GetString()).IsEqualTo("invalid_grant");
            await Assert.That(error.GetProperty("error_description").GetString()).Contains("region");
        }

        await using var restartedGru = await OidcHarness.StartAsync(regions, "gru");
        await using var restartedSjc = await OidcHarness.StartAsync(regions, "sjc");
        foreach (var node in new[] { restartedGru, restartedSjc })
        {
            using var rejected = await node.RefreshAsync(loser);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            var error = await OidcHarness.JsonAsync(rejected);
            await Assert.That(error.GetProperty("error").GetString()).IsEqualTo("invalid_grant");
            await Assert.That(error.GetProperty("error_description").GetString()).Contains("region");
        }

        // Rejecting the losing branch must not revoke the winner's authorization.
        var winner = tokens[outcomes.IndexOf(true)];
        foreach (var node in auth)
        {
            using var live = await node.UserInfoAsync(winner.GetProperty("access_token").GetString()!);
            await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using var refreshed = await sjc.RefreshAsync(winner.GetProperty("refresh_token").GetString()!);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private sealed class TokenAdmissionBarrier
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool armed;

        public Task Reached => this.reached.Task;

        public void Arm() => this.armed = true;

        public void Release() => this.release.TrySetResult();

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (this.armed && context.Request.Path.Value!.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                this.reached.TrySetResult();
                await this.release.Task.WaitAsync(context.RequestAborted);
            }

            await next(context);
        }
    }

}
