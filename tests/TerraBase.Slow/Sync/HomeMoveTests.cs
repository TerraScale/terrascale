using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Testing;
using TerraBase.Tenancy;

namespace TerraBase.Slow.Sync;

public sealed class HomeMoveTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_home_moves_under_writes_without_losing_an_acknowledged_write_and_the_old_home_refuses_writes()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var issuer = new TestIssuer();
        var mesh = new Mesh();
        await using var gru = await StartAsync("gru", issuer, mesh);
        await using var sjc = await StartAsync("sjc", issuer, mesh);
        mesh.Nodes = [("gru", gru), ("sjc", sjc)];

        var organization = await gru.CreateOrganizationAsync("Org " + EntityIds.New());
        var project = await gru.CreateProjectAsync(organization, "Project");
        var bearer = issuer.Mint(gru.User, descriptor =>
        {
            descriptor.Claims["project_id"] = project;
            descriptor.Claims["org_id"] = organization;
            descriptor.Claims["scope"] = "databases:read databases:write sql:read sql:write";
        });
        using var viaGru = gru.WithToken(bearer);
        using var viaSjc = sjc.WithToken(bearer);
        var name = "moving" + Faker.Random.AlphaNumeric(6).ToLowerInvariant();
        using var created = await viaGru.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name, allRegions = true }, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
        var sql = ManagementApi.Uri($"/v1/projects/{project}/databases/{name}/sql/execute");
        using (var table = await viaGru.PostAsJsonAsync(sql, new { statements = new[] { new { sql = "CREATE TABLE acked (id TEXT PRIMARY KEY)" } } }, token))
        {
            await Assert.That(table.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await table.Content.ReadAsStringAsync(token));
        }

        var replication = sjc.Server.Services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(token);
        await replication.PullAllAsync(token);
        await Assert.That(replication.Following.Select(database => database.DatabaseId)).Contains(id);

        // Lease observations carry all-regions mode without an explicit region list. A same-epoch
        // catalog answer must not make that replica stop following or reject a home-session open.
        using (var placement = await viaGru.PatchAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{id}"), new { allRegions = true }, token))
        {
            await Assert.That(placement.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await placement.Content.ReadAsStringAsync(token));
            var tenant = new TenantContext(organization, PlanTier.Hobby, project, id);
            var record = await gru.Server.Services.GetRequiredService<IReplicaCatalog>().FindAsync(tenant, token) is TenantDatabaseRecord found
                ? found : throw new InvalidOperationException("the home placement is missing");
            var epoch = record.Placement.Version;
            await sjc.Server.Services.GetRequiredService<IReplicaCatalog>().UpdatePlacementAsync(
                tenant, new DatabasePlacement("gru", [], epoch, AllRegions: true), token);
        }

        await replication.SyncCatalogsAsync(token);
        await replication.PullAllAsync(token);
        await Assert.That(replication.Following.Select(database => database.DatabaseId)).Contains(id);

        await using var postgres = PostgresSql.Source(gru, DashboardKey(issuer, project), name);
        await using var oldSession = await postgres.OpenConnectionAsync(token);
        await PostgresSql.ExecuteAsync(oldSession, "INSERT INTO acked (id) VALUES ($1)", "before-" + EntityIds.New());

        var acked = new ConcurrentBag<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var beforeMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var homeMoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterMove = 0;
        var writer = Task.Run(async () =>
        {
            var target = viaGru;
            while (!stop.IsCancellationRequested)
            {
                // REST can forward successfully after demotion without returning a 421.
                if (homeMoved.Task.IsCompletedSuccessfully)
                {
                    target = viaSjc;
                }

                var row = EntityIds.New().ToString();
                while (true)
                {
                    using var response = await target.PostAsJsonAsync(sql, new { statements = new[] { new { sql = "INSERT INTO acked (id) VALUES (?)", args = new[] { row } } } }, stop.Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        acked.Add(row);
                        if (acked.Count == 20)
                        {
                            beforeMove.TrySetResult();
                        }

                        if (target == viaSjc && Interlocked.Increment(ref afterMove) == 20)
                        {
                            moved.TrySetResult();
                        }

                        break;
                    }

                    if (response.StatusCode == HttpStatusCode.MisdirectedRequest)
                    {
                        target = viaSjc;
                    }
                    else if (response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests))
                    {
                        throw new InvalidOperationException($"a write failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(token)}");
                    }
                }
            }
        }, token);

        JsonElement body;
        try
        {
            await (await Task.WhenAny(beforeMove.Task, writer).WaitAsync(TimeSpan.FromSeconds(60), token));
            using var move = await viaGru.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{id}/move-home"), new { homeRegion = "sjc" }, token);
            await Assert.That(move.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await move.Content.ReadAsStringAsync(token));
            body = await move.Content.ReadFromJsonAsync<JsonElement>(token);
            homeMoved.SetResult();
            await (await Task.WhenAny(moved.Task, writer).WaitAsync(TimeSpan.FromSeconds(60), token));
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await writer;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // The fixture owns this writer and cancels its final in-flight request.
            }
        }

        await Assert.That(body.GetProperty("homeRegion").GetString()).IsEqualTo("sjc");
        await Assert.That(body.GetProperty("replicaRegions").EnumerateArray().Select(region => region.GetString()!).ToList()).IsEquivalentTo(["gru"]);
        using var read = await viaSjc.PostAsJsonAsync(sql, new { statements = new[] { new { sql = "SELECT id FROM acked" } } }, token);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await read.Content.ReadAsStringAsync(token));
        var stored = (await read.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows").EnumerateArray()
            .Select(row => row[0].GetString()!).ToHashSet(StringComparer.Ordinal);
        await Assert.That(acked.Where(row => !stored.Contains(row))).IsEmpty();
        await Assert.That(acked.Count).IsGreaterThan(20);
        await Assert.That(read.Headers.GetValues("X-TerraBase-Region")).IsEquivalentTo(["sjc"]);

        var refused = await Assert.That(async () => await PostgresSql.ExecuteAsync(oldSession, "INSERT INTO acked (id) VALUES ($1)", "after-" + EntityIds.New()))
            .Throws<PostgresException>();
        await Assert.That(refused!.SqlState).IsEqualTo("08004").Because(refused.MessageText);
    }

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, Mesh mesh) => ManagementApi.StartAsync(
        settings => settings["region"] = region,
        services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, mesh)));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, mesh)));
        },
        sharedIssuer: issuer);

    private static string DashboardKey(TestIssuer issuer, string project)
    {
        var id = EntityIds.New().ToString();
        var key = id + "_" + Faker.Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");
        issuer.GrantClient(key, () => issuer.Mint(id, token =>
        {
            token.Claims["key_id"] = id;
            token.Claims["key_gen"] = TestIssuer.KeyGeneration(key);
            token.Claims["project_id"] = project;
            token.Claims["scope"] = "sql:read sql:write";
        }));
        return key;
    }

    private sealed class Mesh
    {
        public (string Region, ManagementApi Api)[] Nodes { get; set; } = [];
    }

    private sealed class RegionView(string region, Mesh mesh) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. mesh.Nodes.Where(node => node.Region != region).Select(node => new RegionPeer(node.Region, node.Api.Server.BaseAddress))]);
    }

    private sealed class ReplicaView(string region, Mesh mesh) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([.. mesh.Nodes.Where(node => node.Region != region)
                .Select(node => new ReplicaPeer(node.Region, node.Region + "-1", node.Api.Server.BaseAddress))]);
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }
}
