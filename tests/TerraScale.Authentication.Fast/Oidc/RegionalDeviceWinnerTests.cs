using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Oidc;

public sealed class RegionalDeviceWinnerTests
{
    private const string Instance = OidcIssuer.InstanceId;
    private static readonly string[] Collections =
    [
        AuthStore.OidcTokenCollection, AuthStore.OidcAuthorizationCollection, AuthStore.UniqueCollection,
        AuthStore.UserCollection, AuthStore.SessionCollection, AuthStore.OrganizationCollection, AuthStore.MembershipCollection,
    ];

    [Test]
    [Arguments("gru", "sjc", false)]
    [Arguments("sjc", "gru", false)]
    [Arguments("gru", "sjc", true)]
    [Arguments("sjc", "gru", true)]
    public async Task The_last_approval_payload_keeps_its_subject_authorization_and_revocation(string losingRegion, string winningRegion, bool revokeChild)
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        await using var losing = await OidcHarness.StartAsync(regions, losingRegion);
        await using var winning = await OidcHarness.StartAsync(regions, winningRegion);
        var nodes = new[] { losing, winning };
        var loser = await losing.CreateUserAsync("approval-a@example.test");
        var winner = await winning.CreateUserAsync("approval-b@example.test");
        var losingSession = await losing.SignInAsync(loser.Email);
        var winningSession = await winning.SignInAsync(winner.Email);
        using var issued = await winning.Client.PostAsync($"/{OidcIssuer.Slug}/oauth/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = OidcIssuer.CliClientId,
            ["scope"] = "openid email offline_access",
        }));
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var device = await OidcHarness.JsonAsync(issued);
        var userCode = device.GetProperty("user_code").GetString()!;
        var deviceCode = device.GetProperty("device_code").GetString()!;
        await ConvergedAsync(nodes);
        var store = Store(winning);
        var pending = (await winning.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items;
        await Assert.That(pending.Count).IsEqualTo(2);
        var deviceBefore = pending.Single(document => document.Document.GetProperty("type").GetString()!.EndsWith("device_code", StringComparison.Ordinal)).Document;
        var deviceId = deviceBefore.GetProperty("id").GetString()!;
        var rootId = deviceBefore.GetProperty("authorizationId").GetString()!;
        var userCodeId = pending.Single(document => document.Document.GetProperty("id").GetString() != deviceId)
            .Document.GetProperty("id").GetString()!;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admittedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admittedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beginA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beginB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var approvingA = await ApprovalReplicaAsync(regions, losingRegion, losing, admittedA, beginA);
        await using var approvingB = await ApprovalReplicaAsync(regions, winningRegion, winning, admittedB, beginB, reached, release);
        var approvals = new List<Task>();
        string? losingPayload = null;
        try
        {
            // DeviceGate's genuine hosted admissions and rate-limit writes finish while peers are healthy.
            approvals.Add(ApproveAsync(approvingB.Client, winningSession, userCode));
            await admittedB.Task.WaitAsync(TimeSpan.FromSeconds(10));
            approvals.Add(ApproveAsync(approvingA.Client, losingSession, userCode));
            await admittedA.Task.WaitAsync(TimeSpan.FromSeconds(10));
            regions.HoldReplication();

            // Both atomic approvals commit locally before either remote_durable reply completes.
            // B's continuation after RedeemExactlyOnce stays paused until A has written its payload.
            beginB.TrySetResult();
            await LocallyApprovedAsync(store, userCodeId);
            beginA.TrySetResult();
            await LocallyApprovedAsync(Store(losing), userCodeId);
            regions.ReleaseReplication();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await approvals[1];
            losingPayload = (await Store(losing).GetAsync(AuthStore.OidcTokenCollection, Instance, deviceId))!
                .Document.GetProperty("payload").GetString();
        }
        finally
        {
            regions.ReleaseReplication();
            beginA.TrySetResult();
            beginB.TrySetResult();
            release.TrySetResult();
            await Task.WhenAll(approvals);
        }

        await ConvergedAsync(nodes);
        var winningPayload = (await store.GetAsync(AuthStore.OidcTokenCollection, Instance, deviceId))!.Document;
        await Assert.That(winningPayload.GetProperty("payload").GetString()).IsNotEqualTo(losingPayload);
        await Assert.That(winningPayload.GetProperty("subject").GetString()).IsEqualTo(winner.Id);
        foreach (var node in nodes)
        {
            var selected = (await Store(node).GetAsync(AuthStore.OidcTokenCollection, Instance, deviceId))!.Document;
            await Assert.That(selected.GetProperty("payload").GetString()).IsEqualTo(winningPayload.GetProperty("payload").GetString());
            await Assert.That(selected.GetProperty("authorizationId").GetString()).IsEqualTo(rootId);
        }

        // A's manager already saw payload A during approval; it must now redeem converged payload B without a restart.
        using var redeemed = await DeviceTokenAsync(approvingA.Client, deviceCode);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(redeemed);
        await ConvergedAsync(nodes);
        var access = tokens.GetProperty("access_token").GetString()!;
        var childId = new JsonWebToken(access).GetClaim("oi_au_id").Value;
        await Assert.That(childId).IsNotEqualTo(rootId);
        foreach (var node in nodes)
        {
            var authorizations = node.Server.App.Services.GetRequiredService<IOpenIddictAuthorizationStore<OidcAuthorization>>();
            await Assert.That((await authorizations.FindByIdAsync(rootId, CancellationToken.None))!.Subject).IsNull();
            await Assert.That((await authorizations.FindByIdAsync(childId, CancellationToken.None))!.Subject).IsEqualTo(winner.Id);
            await Assert.That((await node.Database.ListDocumentsAsync(AuthStore.OidcAuthorizationCollection)).Items
                .Any(document => document.Document.GetProperty("subject").GetString() == loser.Id)).IsTrue();
            await UsableAsync(node, access, winner.Id);
            using var repeat = await DecideAsync(node.Client, losingSession, userCode);
            await Assert.That(repeat.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        var organization = await winning.CreateOrganizationAsync(access, "Payload B owns this organization");
        await ConvergedAsync(nodes);
        var accesses = new List<string> { access };
        foreach (var node in nodes)
        {
            using var refreshed = await node.PostTokenAsync(new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
                ["client_id"] = OidcIssuer.CliClientId,
            }, null, null, client: node == losing ? approvingA.Client : node.Client);
            await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            tokens = await OidcHarness.JsonAsync(refreshed);
            access = tokens.GetProperty("access_token").GetString()!;
            await Assert.That(new JsonWebToken(access).GetClaim("oi_au_id").Value).IsEqualTo(childId);
            accesses.Add(access);
            await ConvergedAsync(nodes);
            foreach (var replica in nodes)
            {
                await UsableAsync(replica, access, winner.Id);
                var members = await replica.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}/members", access);
                await Assert.That(members.GetProperty("items").EnumerateArray()
                    .Select(member => member.GetProperty("user_id").GetString()!).ToArray()).IsEquivalentTo([winner.Id]);
            }
        }

        var latestRefreshId = (await winning.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items.Single(document =>
            document.Document.GetProperty("type").GetString()!.EndsWith("refresh_token", StringComparison.Ordinal)
            && document.Document.GetProperty("status").GetString() == OpenIddictConstants.Statuses.Valid)
            .Document.GetProperty("id").GetString()!;
        // Successive refreshes warmed both managers. Each case revokes only one authorization on its own genuine grant.
        var authorizationStore = losing.Server.App.Services.GetRequiredService<IOpenIddictAuthorizationStore<OidcAuthorization>>();
        var revokedId = revokeChild ? childId : rootId;
        var authorization = (await authorizationStore.FindByIdAsync(revokedId, CancellationToken.None))!;
        authorization.Status = OpenIddictConstants.Statuses.Revoked;
        await authorizationStore.UpdateAsync(authorization, CancellationToken.None);
        await ConvergedAsync(nodes);
        foreach (var node in nodes)
        {
            var authorizations = node.Server.App.Services.GetRequiredService<IOpenIddictAuthorizationStore<OidcAuthorization>>();
            await Assert.That((await authorizations.FindByIdAsync(revokedId, CancellationToken.None))!.Status)
                .IsEqualTo(OpenIddictConstants.Statuses.Revoked);
            await Assert.That((await authorizations.FindByIdAsync(revokeChild ? rootId : childId, CancellationToken.None))!.Status)
                .IsEqualTo(OpenIddictConstants.Statuses.Valid);
            await Assert.That((await Store(node).GetAsync(AuthStore.OidcTokenCollection, Instance, latestRefreshId))!
                .Document.GetProperty("status").GetString()).IsEqualTo(OpenIddictConstants.Statuses.Valid);
            foreach (var token in accesses)
            {
                using var userinfo = await node.UserInfoAsync(token);
                await Assert.That(userinfo.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                using var management = await node.CallAsync(HttpMethod.Get, Organizations, token);
                await Assert.That(management.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }

            using var refresh = await node.PostTokenAsync(new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
                ["client_id"] = OidcIssuer.CliClientId,
            }, null, null, client: node == losing ? approvingA.Client : node.Client);
            await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await OidcHarness.ErrorAsync(refresh)).IsEqualTo("invalid_grant");
        }
    }

    private static AuthStore Store(OidcHarness node) => node.Server.App.Services.GetRequiredService<AuthStore>();

    private static Task<AuthTestServer> ApprovalReplicaAsync(RegionalTerraBaseFixture regions, string region, OidcHarness node,
        TaskCompletionSource admitted, TaskCompletionSource beginApproval, TaskCompletionSource? reached = null, TaskCompletionSource? release = null)
    {
        var options = node.Server.App.Services.GetRequiredService<AuthOptions>();
        return AuthTestServer.StartAsync(settings: settings =>
        {
            settings["master-key"] = Convert.ToBase64String(options.MasterKey);
            settings["audit-signing-key"] = Convert.ToBase64String(options.AuditSigningKey!);
            settings["dashboard-client-secret"] = OidcHarness.ClientSecret;
            settings["dashboard-redirect-uris"] = OidcHarness.RedirectUri;
            settings["access-token-audiences"] = "https://api.terrascale.tech";
        }, services: services =>
        {
            services.AddSingleton<IEmailSender>(new CapturingEmailSender());
            services.AddSingleton(regions[region].Http);
            services.AddSingleton(regions.Database(region));
            services.AddOpenIddict().AddServer(server => server
                // Handling starts after DeviceGate and OpenIddict's verification authentication have admitted the request.
                .AddEventHandler<OpenIddictServerEvents.HandleEndUserVerificationRequestContext>(handler => handler
                    .SetOrder(int.MinValue)
                    .UseInlineHandler(async context =>
                    {
                        admitted.TrySetResult();
                        await beginApproval.Task.WaitAsync(context.CancellationToken);
                    }))
                .AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler
                    .SetOrder(OpenIddictServerHandlers.RedeemTokenEntry.Descriptor.Order + 1)
                    .UseInlineHandler(async context =>
                    {
                        if (context.EndpointType == OpenIddictServerEndpointType.EndUserVerification && reached is not null && release is not null)
                        {
                            reached.TrySetResult();
                            await release.Task.WaitAsync(context.CancellationToken);
                        }
                    })));
        }, configure: builder => builder.Configuration["TS_REGION"] = region);
    }

    private static async Task LocallyApprovedAsync(AuthStore store, string userCodeId) =>
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
            (await store.GetAsync(AuthStore.OidcTokenCollection, Instance, userCodeId))!.Document.GetProperty("status").GetString() == "redeemed",
            TimeSpan.FromSeconds(10))).IsTrue();

    private static async Task ApproveAsync(HttpClient client, string session, string userCode)
    {
        using var approved = await DecideAsync(client, session, userCode);
        await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await approved.Content.ReadAsStringAsync()).Contains("<h1>Device connected</h1>");
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, string session, string userCode)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, DeviceFlow.PagePath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["user_code"] = userCode, ["decision"] = "approve" }),
        };
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        request.Headers.Add("Cookie", $"tsauth_session={session}");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeviceTokenAsync(HttpClient client, string deviceCode) =>
        client.PostAsync($"/{OidcIssuer.Slug}/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
            ["client_id"] = OidcIssuer.CliClientId,
        }));

    private static async Task UsableAsync(OidcHarness node, string access, string subject)
    {
        using var userinfo = await node.UserInfoAsync(access);
        await Assert.That(userinfo.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await OidcHarness.JsonAsync(userinfo)).GetProperty("sub").GetString()).IsEqualTo(subject);
        using var management = await node.CallAsync(HttpMethod.Get, Organizations, access);
        await Assert.That(management.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task ConvergedAsync(OidcHarness[] nodes)
    {
        foreach (var collection in Collections)
        {
            await Assert.That(await ReadinessWait.UntilAsync(async () =>
            {
                var copies = await Task.WhenAll(nodes.Select(node => node.Database.ListDocumentsAsync(collection)));
                return copies[0].Items.Select(document => document.Id + ":" + document.Version).Order(StringComparer.Ordinal)
                    .SequenceEqual(copies[1].Items.Select(document => document.Id + ":" + document.Version).Order(StringComparer.Ordinal));
            }, TimeSpan.FromSeconds(15))).IsTrue();
        }
    }
}
