using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Regions;
using TerraBase.Features.Platform;
using TerraBase.Tenancy;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Sync;

public sealed class RegionalHandoffTests
{
    private static readonly string[] ReplicaRegions = ["sjc"];
    [Test]
    public async Task A_regional_follower_takes_over_only_after_applying_the_drained_writer_position_and_can_hand_back()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var connection = new NatsConnection(new NatsOpts { Url = nats.Url.ToString(), AuthOpts = new NatsAuthOpts { Token = nats.Token } });
        await connection.ConnectAsync();
        var homes = await new NatsKVContext(new NatsJSContext(connection)).CreateOrUpdateStoreAsync(new NatsKVConfig(HomeLeases.Bucket)
        {
            History = 1,
            LimitMarkerTTL = HomeLeases.Ttl,
        }, token);
        // Replay the exact platform tenant and database IDs used independently in every region.
        var foreignPlatform = new HomeLease(PlatformDatabases.OrganizationId, PlatformDatabases.AuthProjectId,
            PlatformDatabases.AuthId, "sjc", "sjc-2", 3, EntityIds.New().ToString());
        _ = await homes.PutAsync("home." + foreignPlatform.DatabaseId,
            JsonSerializer.SerializeToUtf8Bytes(foreignPlatform, TerraBaseJsonContext.Default.HomeLease), cancellationToken: token);
        using var issuer = new TestIssuer();
        var storage = new FakeS3();
        var directory = Directory.CreateTempSubdirectory("regional-handoff-").FullName;
        using var reservation = new TcpListener(IPAddress.Any, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var peers = Path.Combine(directory, "peers");
        await File.WriteAllTextAsync(peers, "gru-1 gru 127.0.0.2\ngru-2 gru 127.0.0.3\n", token);
        try
        {
            await using var second = await StartAsync("gru-2", "127.0.0.3");
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(second.Server.Services.GetRequiredService<RegionalOwners>().Find("gru")?.Node == "gru-1"), TimeSpan.FromSeconds(10));
            await Assert.That(second.Server.Services.GetRequiredService<RegionalOwners>().Writer).IsFalse();
            await using var first = await StartAsync("gru-1", "127.0.0.2");
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(first.Server.Services.GetRequiredService<RegionalOwners>().Writer), TimeSpan.FromSeconds(10));
            await Assert.That(await first.Server.Services.GetRequiredService<IPlatformCatalog>().EnsurePlatformAsync(token) is TerraBase.Errors.Done).IsTrue();
            await Assert.That(await second.Server.Services.GetRequiredService<RegionalHandoff>().PrimeAsync("gru-1", token)).IsTrue();
            var organization = await first.CreateOrganizationAsync("Org " + EntityIds.New());
            var project = await first.CreateProjectAsync(organization, "Project");
            var bearer = issuer.Mint(first.User, descriptor =>
            {
                descriptor.Claims["project_id"] = project;
                descriptor.Claims["org_id"] = organization;
                descriptor.Claims["scope"] = "databases:read databases:write sql:read sql:write";
            });
            using var viaFirst = first.WithToken(bearer);
            using var viaSecond = second.WithToken(bearer);
            var basePath = $"/v1/projects/{project}/databases";
            using var created = await viaFirst.PostAsJsonAsync(ManagementApi.Uri(basePath), new { name = "rollout", replicaRegions = ReplicaRegions }, token);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
            var database = (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
            var sql = ManagementApi.Uri(basePath + "/" + database + "/sql/execute");
            await ExecuteAsync(viaFirst, "CREATE TABLE acked(id TEXT PRIMARY KEY)");
            await ExecuteAsync(viaFirst, "INSERT INTO acked VALUES ('before')");
            // Regional-only databases can reuse IDs in independent regions.
            // A foreign global lease for that ID must not block promotion here.
            using var regionalOnly = await viaFirst.PostAsJsonAsync(ManagementApi.Uri(basePath), new { name = "regional-only", replicaRegions = Array.Empty<string>() }, token);
            await Assert.That(regionalOnly.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await regionalOnly.Content.ReadAsStringAsync(token));
            var regionalId = (await regionalOnly.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
            await Assert.That(await second.Server.Services.GetRequiredService<RegionalHandoff>().PrimeAsync("gru-1", token)).IsTrue();
            var foreign = new HomeLease(organization, project, regionalId, "sjc", "sjc-1", 1, EntityIds.New().ToString());
            _ = await homes.PutAsync("home." + regionalId, JsonSerializer.SerializeToUtf8Bytes(foreign, TerraBaseJsonContext.Default.HomeLease), cancellationToken: token);
            var foreignDashboard = foreignPlatform with { ProjectId = PlatformDatabases.DashboardProjectId, DatabaseId = PlatformDatabases.DashboardId };
            _ = await homes.PutAsync("home." + foreignDashboard.DatabaseId,
                JsonSerializer.SerializeToUtf8Bytes(foreignDashboard, TerraBaseJsonContext.Default.HomeLease), cancellationToken: token);
            // An ordinary replicated lease is an ordered watch barrier: both nodes
            // must process the preceding foreign replay before these assertions.
            var observed = await homes.GetEntryAsync<byte[]>("home." + database, cancellationToken: token);
            var barrier = JsonSerializer.Deserialize(observed.Value!, TerraBaseJsonContext.Default.HomeLease)!;
            barrier = barrier with { Epoch = barrier.Epoch + 1 };
            _ = await homes.PutAsync("home." + database, JsonSerializer.SerializeToUtf8Bytes(barrier, TerraBaseJsonContext.Default.HomeLease), cancellationToken: token);
            foreach (var api in new[] { first, second })
            {
                var leases = api.Server.Services.GetRequiredService<HomeLeases>();
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(leases.Find(database)?.Epoch == barrier.Epoch), TimeSpan.FromSeconds(5));
                await Assert.That(leases.Find(PlatformDatabases.AuthId)).IsNull();
                await Assert.That(leases.Find(PlatformDatabases.DashboardId)).IsNull();
                await Assert.That(leases.Find(regionalId)).IsNull();
                var catalog = api.Server.Services.GetRequiredService<IReplicaCatalog>();
                var platform = new TenantContext(PlatformDatabases.OrganizationId, PlanTier.Hobby, PlatformDatabases.AuthProjectId, PlatformDatabases.AuthId);
                await Assert.That(await catalog.FindAsync(platform, token) is TenantDatabaseRecord { Placement.Home: "gru", Placement.Replicas.Count: 0, Placement.AllRegions: false }).IsTrue();
                var dashboard = new TenantContext(PlatformDatabases.OrganizationId, PlanTier.Hobby, PlatformDatabases.DashboardProjectId, PlatformDatabases.DashboardId);
                await Assert.That(await catalog.FindAsync(dashboard, token) is TenantDatabaseRecord { Placement.Home: "gru", Placement.Replicas.Count: 0, Placement.AllRegions: false }).IsTrue();
            }
            await Assert.That(await first.Server.Services.GetRequiredService<HomeLeases>().TakeAsync(
                new TenantContext(organization, PlanTier.Hobby, project, regionalId), "sjc", 2, foreign, token)).IsFalse();
            var localPlatform = new TenantContext(PlatformDatabases.OrganizationId, PlanTier.Hobby, PlatformDatabases.AuthProjectId, PlatformDatabases.AuthId);
            await Assert.That(await first.Server.Services.GetRequiredService<HomeLeases>().TakeAsync(localPlatform, "sjc", 4, foreignPlatform, token)).IsFalse();
            await Assert.That(await first.Server.Services.GetRequiredService<HomeLeases>().PrepareAsync(localPlatform, token)).IsTrue();
            // Bound a failed activation so the negative control cannot hang the drill.
            using var activation = CancellationTokenSource.CreateLinkedTokenSource(token);
            activation.CancelAfter(TimeSpan.FromSeconds(8));
            var handoff = first.Server.Services.GetRequiredService<RegionalHandoff>();
            await Assert.That(await handoff.DrainAsync("gru-2", activation.Token)).IsTrue();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(second.Server.Services.GetRequiredService<RegionalOwners>().Writer), TimeSpan.FromSeconds(10));
            await ExecuteAsync(viaSecond, "INSERT INTO acked VALUES ('after')");
            using var read = await viaSecond.PostAsJsonAsync(sql, new { statements = new[] { new { sql = "SELECT count(*) FROM acked" } } }, token);
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await read.Content.ReadAsStringAsync(token));
            await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows")[0][0].GetInt32()).IsEqualTo(2);
            await Assert.That(first.Server.Services.GetRequiredService<RegionalOwners>().Writer).IsFalse();
            // Keep both processes alive: this exercises role reversal without relying on a restart to clear fencing.
            await Assert.That(await second.Server.Services.GetRequiredService<RegionalHandoff>().DrainAsync("gru-1", token)).IsTrue();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(first.Server.Services.GetRequiredService<RegionalOwners>().Writer), TimeSpan.FromSeconds(10));
            await ExecuteAsync(viaFirst, "INSERT INTO acked VALUES ('returned')");
            var retained = await homes.GetEntryAsync<byte[]>("home." + regionalId, cancellationToken: token);
            await Assert.That(JsonSerializer.Deserialize(retained.Value!, TerraBaseJsonContext.Default.HomeLease)).IsEqualTo(foreign);
            foreach (var ignored in new[] { foreignPlatform, foreignDashboard })
            {
                var original = await homes.GetEntryAsync<byte[]>("home." + ignored.DatabaseId, cancellationToken: token);
                await Assert.That(JsonSerializer.Deserialize(original.Value!, TerraBaseJsonContext.Default.HomeLease)).IsEqualTo(ignored);
            }

            async Task ExecuteAsync(HttpClient client, string statement)
            {
                using var response = await client.PostAsJsonAsync(sql, new { statements = new[] { new { sql = statement } } }, token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync(token));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Task<ManagementApi> StartAsync(string node, string address) => ManagementApi.StartAsync(settings =>
        {
            settings["region"] = "gru";
            storage.Configure(settings);
        }, services =>
        {
            storage.Route(services);
            var options = (TerraBaseOptions)services.Last(descriptor => descriptor.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
            services.Replace(ServiceDescriptor.Singleton(options with { MachineId = node, PeersFile = peers, InternalPort = port, NatsUrl = nats.Url, NatsToken = nats.Token }));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Configure<KestrelServerOptions>(kestrel => kestrel.Listen(IPAddress.Parse(address), port, listener =>
            {
                listener.Protocols = HttpProtocols.Http2;
                listener.UseTerraBaseListener(TerraBaseListener.Internal);
            }));
        }, sharedIssuer: issuer);
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }
}
