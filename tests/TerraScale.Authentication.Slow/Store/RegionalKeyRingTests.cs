using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Slow.Store;

/// <summary>Regional key validation, publication, expiry and failure handling through real local storage.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RegionalKeyRingTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Every_jwks_request_publishes_this_regions_copy_and_other_paths_answer_404()
    {
        var (tenant, authority) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own, new SteppingClock(DateTimeOffset.UtcNow), TimeSpan.Zero);
        await new KeyRing(new AuthStore(local, TimeProvider.System), auth.App.Services.GetRequiredService<AuthOptions>(), TimeProvider.System)
            .ProvisionAsync(OidcIssuer.InstanceId, CancellationToken.None);
        var localId = (await local.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Single().Document.GetProperty("id").GetString();
        await new KeyRing(new AuthStore(authority, TimeProvider.System), auth.App.Services.GetRequiredService<AuthOptions>(), TimeProvider.System)
            .ProvisionAsync(OidcIssuer.InstanceId, CancellationToken.None);
        var authorityId = (await authority.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Single().Document.GetProperty("id").GetString();
        // Every supported JWKS path publishes the local key; remote-only keys remain absent.
        await Assert.That(localId).IsNotEqualTo(authorityId);
        foreach (var path in new[] { "/terrascale/.well-known/jwks.json", "/TerraScale/.well-known/jwks.json", "/terrascale/.WELL-KNOWN/jwks.json", "/terrascale/.well-known/jwks.json/" })
        {
            var keys = await auth.Client.GetFromJsonAsync<JsonElement>(path);
            await Assert.That(keys.GetProperty("keys")[0].GetProperty("kid").GetString()).IsEqualTo(localId);
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
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Post })
        {
            using var request = new HttpRequestMessage(method, "/terrascale/.well-known/jwks.json");
            using var response = await auth.Client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task A_remote_only_key_must_not_validate_against_the_local_key_ring()
    {
        var (tenant, authority) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        var clock = new SteppingClock(DateTimeOffset.UtcNow);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own, clock);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, TimeSpan.FromSeconds(15))).IsTrue();
        var options = auth.App.Services.GetRequiredService<AuthOptions>();
        var remoteRing = new KeyRing(new AuthStore(authority, TimeProvider.System), options, TimeProvider.System);
        await remoteRing.ProvisionAsync(OidcIssuer.InstanceId, CancellationToken.None);
        var signingKeys = await remoteRing.GetActiveAsync(OidcIssuer.InstanceId, CancellationToken.None);
        try
        {
            var user = (await new Users(new AuthStore(local, TimeProvider.System), TimeProvider.System)
                .CreateAsync(OidcIssuer.InstanceId, "review@example.test"))!;
            var tokenId = AuthStore.NewId();
            await new AuthStore(local, TimeProvider.System).CreateAsync(AuthStore.OidcTokenCollection, OidcIssuer.InstanceId, tokenId,
                TerraBaseFixture.Json($$"""{"id":"{{tokenId}}","instanceId":"terrascale","subject":"{{user.Id}}","status":"valid","type":"access_token"}"""));
            var issuer = OidcIssuer.Issuer(options).AbsoluteUri;
            var token = Jwt.Sign(signingKeys[0], "at+jwt", issuer, issuer,
                new Dictionary<string, object> { ["sub"] = user.Id, [OidcIssuer.EpochClaim] = "0", ["oi_tkn_id"] = tokenId }, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            clock.Advance(TimeSpan.FromSeconds(61));
            var jwks = await auth.Client.GetFromJsonAsync<JsonElement>("/terrascale/.well-known/jwks.json");
            await Assert.That(jwks.GetProperty("keys")[0].GetProperty("kid").GetString()).IsNotEqualTo(signingKeys[0].KeyId);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/terrascale/organizations");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await auth.Client.SendAsync(request);
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
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own, new SteppingClock(DateTimeOffset.UtcNow));
        // The project replicated; its keys did not.
        var project = AuthStore.NewId();
        foreach (var copy in new[] { local, authority })
        {
            await new AuthStore(copy, TimeProvider.System).CreateAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, project,
                TerraBaseFixture.Json($$"""{"instanceId":"terrascale","id":"{{project}}"}"""));
        }
        var statuses = new List<int>();
        for (var attempt = 0; attempt < KeyRing.MaxKeys + 1; attempt++)
        {
            using var response = await auth.Client.GetAsync($"/{project}/.well-known/jwks.json");
            statuses.Add((int)response.StatusCode);
        }

        var keys = await local.ListDocumentsAsync(AuthStore.SigningKeyCollection);
        var count = keys.Items.Count(key => key.Document.GetProperty("instanceId").GetString() == project);
        using var authorityRead = await auth.Client.GetAsync($"/__test/keys/{project}");
        await Assert.That(statuses).IsEquivalentTo(Enumerable.Repeat(StatusCodes.Status200OK, KeyRing.MaxKeys + 1));
        await Assert.That(count).IsEqualTo(1);
        await Assert.That(authorityRead.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task An_unavailable_local_backend_fails_closed_for_security_reads_and_expired_jwks()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        var clock = new SteppingClock(DateTimeOffset.UtcNow);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own, clock);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, TimeSpan.FromSeconds(15))).IsTrue();
        await new KeyRing(new AuthStore(local, TimeProvider.System), auth.App.Services.GetRequiredService<AuthOptions>(), TimeProvider.System)
            .ProvisionAsync(OidcIssuer.InstanceId, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));
        using var warmed = await auth.Client.GetAsync("/terrascale/.well-known/jwks.json");
        await Assert.That(warmed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        own.Stop();
        clock.Advance(TimeSpan.FromSeconds(61));
        using var failed = await auth.Client.GetAsync("/__test/keys/terrascale");
        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var jwks = await auth.Client.GetAsync("/terrascale/.well-known/jwks.json");
        await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task A_jwks_for_projects_deleted_locally_never_creates_keys()
    {
        // A local deletion must take effect even while another region retains a stale project.
        var (tenant, authority) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own, new SteppingClock(DateTimeOffset.UtcNow));
        var projects = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            var project = AuthStore.NewId();
            foreach (var copy in new[] { local, authority })
            {
                await new AuthStore(copy, TimeProvider.System).CreateAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, project,
                    TerraBaseFixture.Json($$"""{"instanceId":"terrascale","id":"{{project}}"}"""));
            }

            var localStore = new AuthStore(local, TimeProvider.System);
            var record = await localStore.GetAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, project);
            await Assert.That(await localStore.CompareAndDeleteAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, project, record!.Version)).IsTrue();
            projects.Add(project);
        }

        // Several requests per project, concurrently.
        var statuses = await Task.WhenAll(projects.SelectMany(project => Enumerable.Repeat(project, 3)).Select(async project =>
        {
            using var response = await auth.Client.GetAsync($"/{project}/.well-known/jwks.json");
            return response.StatusCode;
        }));

        await Assert.That(statuses.All(status => status == HttpStatusCode.NotFound)).IsTrue();
        foreach (var copy in new[] { local, authority })
        {
            await Assert.That((await copy.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items
                .Count(key => projects.Contains(key.Document.GetProperty("instanceId").GetString()!))).IsEqualTo(0);
        }
    }

    [Test]
    public async Task A_key_read_that_completes_past_the_refresh_interval_is_refused()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        await fixture.CreateCopyAsync(tenant, "auth-local");
        var clock = new SteppingClock(DateTimeOffset.UtcNow);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own, clock);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, TimeSpan.FromSeconds(15))).IsTrue();
        var ring = auth.App.Services.GetRequiredService<KeyRing>();

        // A verification read completing after its freshness deadline cannot authorize a key.
        clock.Advance(TimeSpan.FromSeconds(61));
        own.Hold();
        var late = ring.GetPublicAsync(OidcIssuer.InstanceId, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));
        own.Release();
        var failure = await Assert.ThrowsAsync<ApiException>(() => late);

        await Assert.That(failure!.Status).IsEqualTo(StatusCodes.Status503ServiceUnavailable);
        var fresh = await ring.GetPublicAsync(OidcIssuer.InstanceId, CancellationToken.None);
        await Assert.That(fresh.Count).IsGreaterThan(0);
        fresh.ToList().ForEach(key => key.Dispose());
    }

    [Test]
    public async Task A_security_read_never_joins_a_jwks_read_in_flight()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        var clock = new SteppingClock(DateTimeOffset.UtcNow);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        using var pending = new PausingTransactions(request => request.RequestUri!.AbsolutePath.Contains("/signing_keys/", StringComparison.Ordinal))
        {
            InnerHandler = TerraBaseFixture.NewHandler(),
        };
        using var storage = new HttpClient(pending);
        await using var auth = await StartAsync(tenant, own, clock, storage: storage);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, TimeSpan.FromSeconds(15))).IsTrue();
        await new KeyRing(new AuthStore(local, TimeProvider.System), auth.App.Services.GetRequiredService<AuthOptions>(), TimeProvider.System)
            .ProvisionAsync(OidcIssuer.InstanceId, CancellationToken.None);
        var localId = (await local.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Single().Document.GetProperty("id").GetString();
        clock.Advance(TimeSpan.FromSeconds(61));

        // Gate one SDK request: multiplexed HTTP/2 streams share a relay's blocked TCP read.
        var jwks = await pending.HoldNextAsync(() => auth.Client.GetAsync("/terrascale/.well-known/jwks.json"));
        var checking = auth.Client.GetAsync("/__test/check/terrascale");
        var independent = await Task.WhenAny(checking, Task.Delay(TimeSpan.FromSeconds(5))) == checking;
        var stalled = !jwks.IsCompleted;
        using var published = await pending.ReleaseAsync(jwks);
        using var check = await checking;

        await Assert.That(independent).IsTrue();
        await Assert.That(check.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(stalled).IsTrue();
        await Assert.That((await published.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("keys")[0].GetProperty("kid").GetString()).IsEqualTo(localId);
    }

    private static Task<AuthTestServer> StartAsync(TerraBaseTestTenant tenant, TcpRelay own, SteppingClock clock, TimeSpan? refresh = null, HttpClient? storage = null) =>
        AuthTestServer.StartAsync(
            services: services =>
            {
                TerraBaseFixture.UseRegionalStorage(services, tenant, own.Address, localDatabase: "auth-local");
                if (storage is not null)
                {
                    services.AddSingleton(storage);
                }

                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton(new KeyRingRefresh(refresh ?? TimeSpan.FromMinutes(1)));
            },
            beforeStart: services => SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None),
            routes: app =>
            {
                app.MapGet("/__test/keys/{instance}", async (string instance, KeyRing ring, CancellationToken cancellationToken) =>
                {
                    var keys = await ring.GetPublicAsync(instance, cancellationToken);
                    foreach (var key in keys)
                    {
                        key.Dispose();
                    }

                    return Results.Ok();
                });
                app.MapGet("/__test/check/{instance}", async (string instance, KeyRing ring, CancellationToken cancellationToken) =>
                {
                    await ring.CheckPublicMetadataAsync(instance, cancellationToken);
                    return Results.Ok();
                });
            });
}
