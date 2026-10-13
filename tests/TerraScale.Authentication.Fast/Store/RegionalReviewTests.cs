using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Store;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RegionalReviewTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Only_exact_discovery_paths_can_select_local_storage()
    {
        var (tenant, authority) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own, other, gru, new SteppingClock(DateTimeOffset.UtcNow), TimeSpan.Zero);
        await new KeyRing(new AuthStore(local, TimeProvider.System), auth.App.Services.GetRequiredService<AuthOptions>(), TimeProvider.System)
            .ProvisionAsync(OidcIssuer.InstanceId, CancellationToken.None);
        var localId = (await local.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Single().Document.GetProperty("id").GetString();
        var authorityId = (await authority.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Single().Document.GetProperty("id").GetString();
        foreach (var (path, expected) in new[]
        {
            ("/terrascale/.well-known/jwks.json", localId),
            ("/TerraScale/.well-known/jwks.json", localId),
            ("/terrascale/.WELL-KNOWN/jwks.json", authorityId),
            ("/terrascale/.well-known/jwks.json/", authorityId),
        })
        {
            var keys = await auth.Client.GetFromJsonAsync<JsonElement>(path);
            await Assert.That(keys.GetProperty("keys")[0].GetProperty("kid").GetString()).IsEqualTo(expected);
        }

        foreach (var path in new[]
        {
            "/terrascale/.well-known/jwks.json/extra",
            "/unknown/.well-known/jwks.json",
            "/terrascale%2Foauth%2Ftoken/.well-known/jwks.json",
            "/terrascale/.well-known%2Fjwks.json",
            "/terrascale/oauth/userinfo/.well-known/jwks.json",
        })
        {
            using var response = await auth.Client.GetAsync(path);
            Console.WriteLine($"Rejected path: {path} -> {(int)response.StatusCode}");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Post })
        {
            using var request = new HttpRequestMessage(method, "/terrascale/.well-known/jwks.json");
            using var response = await auth.Client.SendAsync(request);
            Console.WriteLine($"Discovery method: {method} -> {(int)response.StatusCode}");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task A_local_jwks_snapshot_must_not_validate_a_key_absent_from_gru()
    {
        var (tenant, authority) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        var clock = new SteppingClock(DateTimeOffset.UtcNow);
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own, other, gru, clock);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, TimeSpan.FromSeconds(15))).IsTrue();
        var options = auth.App.Services.GetRequiredService<AuthOptions>();
        var localRing = new KeyRing(new AuthStore(local, TimeProvider.System), options, TimeProvider.System);
        await localRing.ProvisionAsync(OidcIssuer.InstanceId, CancellationToken.None);
        var signingKeys = await localRing.GetActiveAsync(OidcIssuer.InstanceId, CancellationToken.None);
        try
        {
            var user = (await new Users(new AuthStore(authority, TimeProvider.System), TimeProvider.System)
                .CreateAsync(OidcIssuer.InstanceId, "review@example.test"))!;
            var tokenId = AuthStore.NewId();
            await new AuthStore(authority, TimeProvider.System).CreateAsync(AuthStore.OidcTokenCollection, OidcIssuer.InstanceId, tokenId,
                TerraBaseFixture.Json($$"""{"id":"{{tokenId}}","instanceId":"terrascale","subject":"{{user.Id}}","status":"valid","type":"access_token"}"""));
            var issuer = OidcIssuer.Issuer(options).AbsoluteUri;
            var token = Jwt.Sign(signingKeys[0], "at+jwt", issuer, issuer,
                new Dictionary<string, object> { ["sub"] = user.Id, [OidcIssuer.EpochClaim] = "0", ["oi_tkn_id"] = tokenId }, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            clock.Advance(TimeSpan.FromSeconds(61));
            var jwks = await auth.Client.GetFromJsonAsync<JsonElement>("/terrascale/.well-known/jwks.json");
            await Assert.That(jwks.GetProperty("keys")[0].GetProperty("kid").GetString()).IsEqualTo(signingKeys[0].KeyId);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/terrascale/organizations");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await auth.Client.SendAsync(request);
            Console.WriteLine($"Local-only key platform validation: {(int)response.StatusCode}; key {signingKeys[0].KeyId} is absent from authority");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        finally
        {
            foreach (var key in signingKeys)
            {
                key.Dispose();
            }
        }
    }

    [Test]
    public async Task A_new_project_jwks_must_not_create_unbounded_keys_while_its_local_copy_lags()
    {
        var (tenant, authority) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own, other, gru, new SteppingClock(DateTimeOffset.UtcNow));
        var project = AuthStore.NewId();
        await new AuthStore(local, TimeProvider.System).CreateAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, project,
            TerraBaseFixture.Json($$"""{"instanceId":"terrascale","id":"{{project}}"}"""));
        var statuses = new List<int>();
        for (var attempt = 0; attempt < KeyRing.MaxKeys + 1; attempt++)
        {
            using var response = await auth.Client.GetAsync($"/{project}/.well-known/jwks.json");
            statuses.Add((int)response.StatusCode);
        }

        var keys = await authority.ListDocumentsAsync(AuthStore.SigningKeyCollection);
        var count = keys.Items.Count(key => key.Document.GetProperty("instanceId").GetString() == project);
        using var authorityRead = await auth.Client.GetAsync($"/__review/keys/{project}");
        Console.WriteLine($"New project requests: {string.Join(',', statuses)}; authority project key count: {count}; authority key read: {(int)authorityRead.StatusCode}");
        await Assert.That(count).IsEqualTo(1);
        await Assert.That(authorityRead.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task An_authority_read_failure_must_not_poison_a_healthy_local_jwks()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        var clock = new SteppingClock(DateTimeOffset.UtcNow);
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own, other, gru, clock);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, TimeSpan.FromSeconds(15))).IsTrue();
        await new KeyRing(new AuthStore(local, TimeProvider.System), auth.App.Services.GetRequiredService<AuthOptions>(), TimeProvider.System)
            .ProvisionAsync(OidcIssuer.InstanceId, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));
        using var warmed = await auth.Client.GetAsync("/terrascale/.well-known/jwks.json");
        await Assert.That(warmed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        gru.Stop();
        clock.Advance(TimeSpan.FromSeconds(61));
        using var failed = await auth.Client.GetAsync("/__review/keys/terrascale");
        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var jwks = await auth.Client.GetAsync("/terrascale/.well-known/jwks.json");
        Console.WriteLine($"JWKS with healthy local storage after gru failed: {(int)jwks.StatusCode}");
        await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static Task<AuthTestServer> StartAsync(TerraBaseTestTenant tenant, TcpRelay own, TcpRelay other, TcpRelay gru, SteppingClock clock, TimeSpan? refresh = null) =>
        AuthTestServer.StartAsync(
            services: services =>
            {
                TerraBaseFixture.UseRegionalStorage(services, tenant, own.Address, other, gru.Address, localDatabase: "auth-local");
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton(new KeyRingRefresh(refresh ?? TimeSpan.FromMinutes(1)));
            },
            beforeStart: services => SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None),
            routes: app => app.MapGet("/__review/keys/{instance}", async (string instance, KeyRing ring, CancellationToken cancellationToken) =>
            {
                var keys = await ring.GetPublicAsync(instance, cancellationToken);
                foreach (var key in keys)
                {
                    key.Dispose();
                }

                return Results.Ok();
            }));
}
