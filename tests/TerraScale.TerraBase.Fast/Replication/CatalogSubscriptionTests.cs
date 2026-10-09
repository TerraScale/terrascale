using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class CatalogSubscriptionTests
{
    [Test]
    public async Task Wrong_region_JWT_subscriptions_on_a_catalog_follower_do_not_create_a_local_database()
    {
        using var peers = new PeerTls();
        await using var primary = await ManagementApi.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["region"] = "sfo-1";
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var faker = new Faker();
        var organization = await primary.CreateOrganizationAsync(faker.Company.CompanyName());
        var project = await primary.CreateProjectAsync(organization, "main");
        await primary.VerifyProjectBindingAsync(organization, project);
        var database = new TenantContext(organization, PlanTier.Hobby, project, EntityIds.New().ToString());
        // The region's catalog primary records a database served elsewhere, as WAN discovery does: no local file.
        await Assert.That(await primary.Server.Services.GetRequiredService<ITenantDirectory>()
            .AdoptDatabaseAsync(database, "orders", new DatabasePlacement("gru-1", [], 1), deadline.Token)).IsEqualTo(DatabaseAdoption.Registered);
        var credentials = Path.Combine(primary.Server.DataDirectory, "user.creds");
        await File.WriteAllTextAsync(credentials,
            "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n", deadline.Token);
        var network = new FakeRegionNetwork();
        var creations = 0;
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            primary.Tls.Apply(settings);
            TestIssuer.Configure(settings);
            settings["region"] = "sfo-1";
            settings["node-name"] = "subscription-follower";
            settings["catalog-primary"] = primary.Server.PeerAddress!.ToString();
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }, services =>
        {
            primary.Issuer.Route(services);
            services.AddSingleton(network.Join("sfo"));
            services.AddSingleton<IMemberStore>(new FakeMemberStore(TimeProvider.System));
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, tenant) =>
            {
                if (step == "create.published" && tenant.DatabaseId == database.DatabaseId)
                {
                    Interlocked.Increment(ref creations);
                }
            });
        });
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var ready = await follower.Client.GetAsync(new Uri("/readyz", UriKind.Relative), token);
            return ready.StatusCode == HttpStatusCode.OK;
        });
        var engine = follower.Services.GetRequiredService<IStorageEngine>();
        await Assert.That(await engine.OpenDatabaseAsync(database, deadline.Token)).IsNull();

        var route = new Uri($"/projects/{project}/databases/orders/collections/items/subscribe", UriKind.Relative);
        foreach (var (grantProject, grantDatabase, scope, expired, status, code) in new[]
        {
            (project, "orders", "shapes:read", false, HttpStatusCode.MisdirectedRequest, "wrong_region"),
            (EntityIds.New().ToString(), "orders", "shapes:read", false, HttpStatusCode.NotFound, "unknown_database"),
            (project, "other", "shapes:read", false, HttpStatusCode.NotFound, "unknown_database"),
            (project, "orders", "documents:read", false, HttpStatusCode.Forbidden, "forbidden_scope"),
            (project, "orders", "shapes:read", true, HttpStatusCode.Unauthorized, "unauthorized"),
        })
        {
            // The subject is not a catalog member; only the signed project/database grants authorize this route.
            using var client = primary.Tls.CreateClient(follower.ClientAddress!, primary.Issuer.Mint(EntityIds.New().ToString(), token =>
            {
                token.Claims["project_id"] = grantProject;
                token.Claims["database"] = grantDatabase;
                token.Claims["scope"] = scope;
                if (expired)
                {
                    token.IssuedAt = DateTime.UtcNow.AddMinutes(-1);
                    token.Expires = DateTime.UtcNow.AddSeconds(-10);
                }
            }));
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await Assert.That(response.StatusCode).IsEqualTo(status);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
            await Assert.That(response.Headers.Contains("X-TerraBase-Database-Home")).IsEqualTo(status == HttpStatusCode.MisdirectedRequest);
            await Assert.That(response.Headers.Contains("X-TerraBase-Database-Regions")).IsEqualTo(status == HttpStatusCode.MisdirectedRequest);
            if (status == HttpStatusCode.MisdirectedRequest)
            {
                await Assert.That(response.Headers.GetValues("X-TerraBase-Database-Home").Single()).IsEqualTo("gru-1");
                await Assert.That(response.Headers.GetValues("X-TerraBase-Database-Regions").Single()).IsEqualTo("gru-1");
            }
        }

        await Assert.That(Volatile.Read(ref creations)).IsEqualTo(0);
        await Assert.That(await engine.OpenDatabaseAsync(database, deadline.Token)).IsNull();
    }
}
