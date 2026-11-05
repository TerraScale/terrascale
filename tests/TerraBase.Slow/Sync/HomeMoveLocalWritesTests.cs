using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.HomeApply;
using TerraBase.Features.HomePush;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Sync;

public sealed class HomeMoveLocalWritesTests
{
    [Test]
    public async Task Twenty_home_moves_preserve_every_remote_local_ack_and_keep_the_remote_replica_readable()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        using var issuer = new TestIssuer();
        var storage = new FakeS3();
        var mesh = new Mesh();
        await using var gru = await StartAsync("gru", issuer, storage, mesh, nats);
        await using var sjc = await StartAsync("sjc", issuer, storage, mesh, nats);
        await using var fra = await StartAsync("fra", issuer, storage, mesh, nats);
        mesh.Nodes = [("gru", gru), ("sjc", sjc), ("fra", fra)];
        var organization = await gru.CreateOrganizationAsync("Move " + EntityIds.New());
        var project = await gru.CreateProjectAsync(organization, "Project");
        await gru.SetPlanAsync(gru.User, organization, "hobby", token);
        var bearer = issuer.Mint(gru.User, descriptor =>
        {
            descriptor.Claims["project_id"] = project;
            descriptor.Claims["org_id"] = organization;
            descriptor.Claims["scope"] = "databases:read databases:write sql:read sql:write";
        });
        using var viaGru = gru.WithToken(bearer);
        using var viaSjc = sjc.WithToken(bearer);
        using var viaFra = fra.WithToken(bearer);
        viaSjc.DefaultRequestHeaders.Add("X-TerraScale-Region-Pin", "sjc");
        var name = "move" + EntityIds.New().ToString().ToLowerInvariant();
        using var created = await viaGru.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name, allRegions = true }, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
        var tenant = new TenantContext(organization, PlanTier.Hobby, project, id);
        var sql = ManagementApi.Uri($"/v1/projects/{project}/databases/{name}/sql/execute");
        using (var table = await ExecuteAsync(viaGru, sql, "CREATE TABLE acked (id TEXT PRIMARY KEY)", token))
        {
            await Assert.That(table.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await table.Content.ReadAsStringAsync(token));
        }

        foreach (var node in new[] { sjc, fra })
        {
            await node.Server.Services.GetRequiredService<RegionReplication>().SyncCatalogsAsync(token);
            await node.Server.Services.GetRequiredService<RegionReplication>().PullAllAsync(token);
        }

        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(mesh.Nodes.All(node => node.Api.Server.Services.GetRequiredService<HomePush>().Started)), TimeSpan.FromSeconds(30));
        var expected = new ConcurrentBag<string>();
        var local = sjc.Server.Services.GetRequiredService<ILocalWrites>();
        for (var round = 0; round < 20; round++)
        {
            var old = round % 2 == 0 ? gru : fra;
            var target = round % 2 == 0 ? fra : gru;
            var viaOld = round % 2 == 0 ? viaGru : viaFra;
            var viaTarget = round % 2 == 0 ? viaFra : viaGru;
            var targetRegion = round % 2 == 0 ? "fra" : "gru";
            old.Server.Services.GetRequiredService<DelayedApply>().Hold = true;
            target.Server.Services.GetRequiredService<DelayedApply>().Hold = false;
            for (var before = 0; before < 2; before++)
            {
                var row = EntityIds.New().ToString();
                using var response = await ExecuteAsync(viaSjc, sql, $"INSERT INTO acked (id) VALUES ('{row}')", token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync(token));
                await Assert.That(string.Join(";", response.Headers.GetValues("TerraScale-Write-Route"))).Contains("commit=local");
                expected.Add(row);
            }

            // A remote commit can publish to the old home's subject after its fence. Keep one
            // transaction open across promotion, independently of catalog refresh timing.
            await using var delayed = await local.BeginAsync(tenant, token) ?? throw new InvalidOperationException("SJC lost its local commit path");
            var lateId = EntityIds.New().ToString();
            _ = await delayed.ExecuteAsync(new SqlStatement($"INSERT INTO acked (id) VALUES ('{lateId}')", [], new Dictionary<string, object?>(), 100, 1 << 20, TimeSpan.FromSeconds(5)), token);
            var writer = Task.Run(async () =>
            {
                // The open local writer serializes this load until its delayed commit below.
                for (var write = 0; write < 5; write++)
                {
                    var row = EntityIds.New().ToString();
                    using var response = await ExecuteAsync(viaSjc, sql, $"INSERT INTO acked (id) VALUES ('{row}')", token);
                    Console.WriteLine($"move={round + 1} attempt={write + 1} status={(int)response.StatusCode} id={row}");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        expected.Add(row);
                    }
                    else
                    {
                        await Assert.That(response.StatusCode is HttpStatusCode.MisdirectedRequest or HttpStatusCode.ServiceUnavailable or HttpStatusCode.Conflict).IsTrue()
                            .Because(await response.Content.ReadAsStringAsync(token));
                    }
                }
            }, token);
            using var move = await viaOld.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{id}/move-home"), new { homeRegion = targetRegion }, token);
            await Assert.That(move.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await move.Content.ReadAsStringAsync(token));
            var late = await delayed.CommitAsync(homeDurable: false, token);
            await Assert.That(late.Id).IsNotNull();
            expected.Add(lateId);
            // A forwarded attempt can already be waiting at the old home's 30-second
            // ordering barrier when it is fenced. Allow its refusal to finish too.
            await writer.WaitAsync(TimeSpan.FromSeconds(60), token);
            string[] observed = [];
            var readStatus = HttpStatusCode.OK;
            try
            {
                await TerraBaseTestServer.EventuallyAsync(async cancellation =>
                {
                    using var read = await ExecuteAsync(viaTarget, sql, "SELECT id FROM acked ORDER BY id", cancellation);
                    readStatus = read.StatusCode;
                    if (read.StatusCode != HttpStatusCode.OK)
                    {
                        return false;
                    }

                    observed = Rows(await read.Content.ReadFromJsonAsync<JsonElement>(cancellation));
                    return observed.Length == expected.Count && observed.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
                }, TimeSpan.FromSeconds(45));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new InvalidOperationException($"move={round + 1} status={readStatus} expected={string.Join(',', expected)} actual={string.Join(',', observed)}");
            }
            var replication = sjc.Server.Services.GetRequiredService<RegionReplication>();
            await replication.SyncCatalogsAsync(token);
            await replication.PullAllAsync(token);
            using var pinned = await ExecuteAsync(viaSjc, sql, "SELECT id FROM acked ORDER BY id", token);
            await Assert.That(pinned.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await pinned.Content.ReadAsStringAsync(token));
            await Assert.That(pinned.Headers.GetValues("X-TerraBase-Region")).IsEquivalentTo(["sjc"]);
            var actual = Rows(await pinned.Content.ReadFromJsonAsync<JsonElement>(token));
            await Assert.That(actual.Length).IsEqualTo(expected.Count);
            await Assert.That(actual).IsEquivalentTo(expected);
            Console.WriteLine($"move={round + 1} home={targetRegion} acknowledged={expected.Count} missing=0 duplicates=0 sjc=local origin_sequence={local.LastPushed(tenant)} ids={string.Join(',', expected.Order(StringComparer.Ordinal))}");
            await old.Server.Services.GetRequiredService<RegionReplication>().SyncCatalogsAsync(token);
            await old.Server.Services.GetRequiredService<RegionReplication>().PullAllAsync(token);
        }
    }

    private static string[] Rows(JsonElement answer) => [.. answer.GetProperty("results")[0].GetProperty("rows").EnumerateArray().Select(row => row[0].GetString()!)];

    private static Task<HttpResponseMessage> ExecuteAsync(HttpClient client, Uri path, string sql, CancellationToken token) =>
        client.PostAsJsonAsync(path, new { statements = new[] { new { sql } } }, token);

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, FakeS3 storage, Mesh mesh, JetStreamServer nats) => ManagementApi.StartAsync(
        settings => { settings["region"] = region; storage.Configure(settings); },
        services =>
        {
            storage.Route(services);
            var options = (TerraBaseOptions)services.Last(descriptor => descriptor.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
            services.Replace(ServiceDescriptor.Singleton(options with { MachineId = region + "-1", NatsUrl = nats.Url, NatsToken = nats.Token }));
            services.AddSingleton<HomeApplier>();
            services.AddSingleton<DelayedApply>();
            services.Replace(ServiceDescriptor.Singleton<IHomeApply>(provider => provider.GetRequiredService<DelayedApply>()));
            services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, mesh)));
            services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, mesh)));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
        }, sharedIssuer: issuer);

    private sealed class DelayedApply(HomeApplier inner) : IHomeApply
    {
        public volatile bool Hold;
        public ValueTask<HomeApplyResult?> ApplyAsync(TenantContext database, RegionTransaction transaction, CancellationToken cancellationToken) =>
            Hold ? ValueTask.FromResult<HomeApplyResult?>(null) : inner.ApplyAsync(database, transaction, cancellationToken);
        public ValueTask<HomeApplyResult?> StateAsync(TenantContext database, string transactionId, CancellationToken cancellationToken) => inner.StateAsync(database, transactionId, cancellationToken);
        public ValueTask PrepareAsync(TenantContext database, CancellationToken cancellationToken) => inner.PrepareAsync(database, cancellationToken);
        public ValueTask StampHomeWritesAsync(IStorageTransaction transaction, CancellationToken cancellationToken) => inner.StampHomeWritesAsync(transaction, cancellationToken);
    }

    private sealed class Mesh { public (string Region, ManagementApi Api)[] Nodes { get; set; } = []; }
    private sealed class RegionView(string region, Mesh mesh) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. mesh.Nodes.Where(node => node.Region != region).Select(node => new RegionPeer(node.Region, node.Api.Server.BaseAddress))]);
    }
    private sealed class ReplicaView(string region, Mesh mesh) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([.. mesh.Nodes.Where(node => node.Region != region).Select(node => new ReplicaPeer(node.Region, node.Region + "-1", node.Api.Server.BaseAddress))]);
    }
    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc", "fra"]);
    }
}
