using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Backup;
using TerraBase.Features.Memory;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class RegionShapeTailTests
{
    private static readonly string[] Projected = ["name", "score"];
    private static readonly string[] Tables = ["items"];
    private static readonly string[] GrantedColumns = ["id", "owner_id", "body"];
    private static readonly string[] AliceFirst = ["1"];
    private static readonly string[] AliceNew = ["4"];
    private static readonly string[] NotesTable = ["notes"];

    [Test]
    public async Task A_shape_in_sjc_reads_gru_updates_and_filtered_moves_from_its_replica_even_after_home_disconnects()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var issuer = new TestIssuer();
        var regions = new List<RegionPeer>();
        var backupTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var backups = new FakeS3();
        await using var gru = await StartAsync("gru", issuer, regions, backups, backupTime);
        regions.Add(new RegionPeer("gru", gru.Server.BaseAddress));
        await using var sjc = await StartAsync("sjc", issuer, regions);
        regions.Add(new RegionPeer("sjc", sjc.Server.BaseAddress));
        var organization = await gru.CreateOrganizationAsync("Org " + EntityIds.New());
        await gru.SetPlanAsync(gru.User, organization, "enterprise", token);
        var project = await gru.CreateProjectAsync(organization, "Project");
        using var created = await gru.Client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main", replicaRegions = new List<string> { "sjc" } }, token);
        created.EnsureSuccessStatusCode();
        var database = new ManagedDatabaseRef(organization, project, (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!, "main");
        var bearer = gru.ProjectToken(project, "databases:read databases:write sql:read sql:write");
        using var home = gru.DataClient(bearer);
        using var local = sjc.DataClient(bearer);
        using var seeded = await home.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"),
            new { statements = new object[] { new { sql = "CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT, score INTEGER)" }, new { sql = "INSERT INTO items VALUES (1, 'first', 0), (2, 'second', 20)" } } }, token);
        seeded.EnsureSuccessStatusCode();
        using var grant = await home.PutAsJsonAsync(ManagementApi.Uri(database.Data + "/tables/items/read-grant"),
            new { where = "score >= 10", columns = Projected }, token);
        grant.EnsureSuccessStatusCode();
        using var browser = sjc.WithToken(issuer.Mint(EntityIds.New().ToString(), claims =>
        {
            claims.Claims["token_kind"] = "end_user";
            claims.Claims["project_id"] = project;
            claims.Claims["instance_id"] = project;
            claims.Claims["database"] = database.Id;
            claims.Claims["roles"] = Array.Empty<string>();
            claims.Claims["tables"] = Tables;
        }));
        var replication = sjc.Server.Services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(token);
        await replication.PullAllAsync(token);
        var backup = gru.Server.Services.GetRequiredService<IContinuousBackup>();
        await backup.RunOnceAsync(token);
        var path = ManagementApi.Uri(database.Data).ToString();
        var shapes = new[]
        {
            new TableShapeClient(local, path, "items"),
            new TableShapeClient(local, path, "items", "&where=score%3E%3D10&columns=name,score"),
            new TableShapeClient(browser, path, "items"),
        };
        foreach (var shape in shapes)
        {
            await shape.SyncAsync();
        }

        await replication.PullAllAsync(token);
        await replication.PullAllAsync(token);
        var tenant = new TenantContext(organization, PlanTier.Hobby, project, database.Id);
        var store = await gru.Server.StoreAsync(tenant);
        var replicas = sjc.Server.Services.GetRequiredService<IDatabaseReplicas>();
        Task<long> ReadReplicaFloorAsync() => store.WithWriterAsync(writer =>
        {
            using var command = writer.Connection.CreateCommand();
            command.CommandText = "SELECT end_offset FROM _tb_shape_chunk WHERE handle = '~cdc:replica:sjc' AND offset = 0;";
            return (long)command.ExecuteScalar()!;
        }, "the persisted replica CDC floor", token);
        var replicaFloor = await ReadReplicaFloorAsync();
        await Assert.That(replicaFloor).IsGreaterThan(0);
        for (var round = 0; round < 3; round++)
        {
            var held = replicas.HeldRevision(tenant)!.Value;
            var targets = shapes.Select(shape => shape.Uri(shape.NextOffset, live: true)).ToArray();
            var parked = shapes.Select((shape, index) => shape.GetAsync(targets[index])).ToArray();
            using var changed = await home.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"),
                new
                {
                    statements = new object[]
                    {
                        new { sql = $"UPDATE items SET score = 30, name = 'round-{round}' WHERE id = 1" },
                        new { sql = "UPDATE items SET score = 0 WHERE id = 2" },
                    },
                }, token);
            changed.EnsureSuccessStatusCode();
            backupTime.Advance(TimeSpan.FromSeconds(3));
            await backup.RunOnceAsync(token);
            foreach (var shape in shapes)
            {
                var boundary = await store.WithWriterAsync(writer =>
                {
                    using var command = writer.Connection.CreateCommand();
                    command.CommandText = "SELECT count(*) FROM turso_cdc WHERE change_id = $offset AND change_type = 2;";
                    command.Parameters.AddWithValue("$offset", long.Parse(shape.NextOffset, CultureInfo.InvariantCulture));
                    return (long)command.ExecuteScalar()!;
                }, "the advertised continuation after backup cleanup", token);
                await Assert.That(boundary).IsEqualTo(1L);
            }

            if (round == 1)
            {
                using var push = await RevisionPush.BuildAsync(gru.Server.Services.GetRequiredService<IDatabaseRevisions>(),
                    gru.Server.Services.GetRequiredService<IMemoryBudget>(), tenant, held.ToString(), 0, token);
                await Assert.That(RevisionPush.TryRead(push!.Payload.ToArray(), out var notice, out var answer)).IsTrue();
                await Assert.That(DatabaseRevision.TryParse(notice!.Revision, out var target)).IsTrue();
                await Assert.That(replicas.Offer(tenant, held, target, answer, notice.CdcPosition)).IsTrue();
                await Assert.That((await replicas.PullAsync(tenant, fromHome: false, token))!.Pushed).IsTrue();
                _ = await Task.WhenAll(parked).WaitAsync(TimeSpan.FromSeconds(10), token);
            }

            await replication.PullAllAsync(token);
            await replication.PullAllAsync(token);
            await Assert.That(await ReadReplicaFloorAsync()).IsGreaterThan(replicaFloor);
            var boundaries = await store.WithWriterAsync(writer =>
            {
                using var command = writer.Connection.CreateCommand();
                command.CommandText = "SELECT change_id FROM turso_cdc WHERE change_type = 2;";
                using var reader = command.ExecuteReader();
                var result = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    result.Add(reader.GetInt64(0).ToString(CultureInfo.InvariantCulture));
                }

                return result;
            }, "home CDC boundaries", token);
            if (round == 2)
            {
                await gru.DisposeAsync();
            }

            for (var index = 0; index < shapes.Length; index++)
            {
                var reply = await parked[index].WaitAsync(TimeSpan.FromSeconds(10), token);
                var first = reply;
                List<(string, string, string)> moves = [];
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.OK).Because(System.Text.Encoding.UTF8.GetString(reply.Body));
                    await Assert.That(boundaries.Contains(reply.NextOffset!)).IsTrue().Because("replicas advertise only home commit boundaries");
                    if (round == 0 && index > 0)
                    {
                        moves.AddRange(reply.Ops.Select(op => (op.GetProperty("op").GetString()!, op.GetProperty("key").GetString()!, op.GetProperty("move").GetString()!)));
                    }

                    shapes[index].Apply(reply);
                    if (shapes[index].State.TryGetValue("1", out var row) && row["name"]!.GetValue<string>() == $"round-{round}")
                    {
                        break;
                    }

                    reply = await shapes[index].LiveAsync(shapes[index].NextOffset).WaitAsync(TimeSpan.FromSeconds(10), token);
                }

                if (round == 0 && index > 0)
                {
                    await Assert.That(moves).IsEquivalentTo([("insert", "1", "in"), ("delete", "2", "out")]);
                }

                await Assert.That(shapes[index].State["1"]["name"]!.GetValue<string>()).IsEqualTo($"round-{round}");
                if (index > 0)
                {
                    await Assert.That(shapes[index].State.Keys).IsEquivalentTo(["1"]);
                    await Assert.That(shapes[index].State["1"].ContainsKey("id")).IsFalse();
                }

                var repeated = await shapes[index].GetAsync(targets[index]);
                await Assert.That(repeated.Body).IsEquivalentTo(first.Body);
            }
        }
    }

    [Test]
    public async Task An_end_user_tails_only_their_granted_rows_from_the_sjc_replica()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var issuer = new TestIssuer();
        var regions = new List<RegionPeer>();
        await using var gru = await StartAsync("gru", issuer, regions);
        regions.Add(new RegionPeer("gru", gru.Server.BaseAddress));
        await using var sjc = await StartAsync("sjc", issuer, regions);
        regions.Add(new RegionPeer("sjc", sjc.Server.BaseAddress));
        var organization = await gru.CreateOrganizationAsync("Org " + EntityIds.New());
        var project = await gru.CreateProjectAsync(organization, "Project");
        using var created = await gru.Client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main", replicaRegions = new List<string> { "sjc" } }, token);
        created.EnsureSuccessStatusCode();
        var database = new ManagedDatabaseRef(organization, project, (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!, "main");
        using var owner = gru.DataClient(gru.ProjectToken(project, "databases:read databases:write sql:read sql:write"));
        var (alice, bob) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        using var seeded = await owner.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"), new
        {
            statements = new object[]
            {
                new { sql = "CREATE TABLE notes(id INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, body TEXT, secret TEXT)" },
                new { sql = "INSERT INTO notes VALUES (1, ?, 'a1', 's'), (2, ?, 'b1', 's')", args = new[] { alice, bob } },
            },
        }, token);
        seeded.EnsureSuccessStatusCode();
        using var granted = await owner.PutAsJsonAsync(ManagementApi.Uri(database.Data + "/tables/notes/read-grant"),
            new { where = "owner_id = :sub", columns = GrantedColumns }, token);
        await Assert.That(granted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var replication = sjc.Server.Services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(token);
        await replication.PullAllAsync(token);
        await replication.PullAllAsync(token);

        var endUser = issuer.Mint(alice, jwt =>
        {
            jwt.Claims["token_kind"] = "end_user";
            jwt.Claims["project_id"] = project;
            jwt.Claims["instance_id"] = project;
            jwt.Claims["database"] = database.Id;
            jwt.Claims["roles"] = Array.Empty<string>();
            jwt.Claims["tables"] = NotesTable;
        });
        using var home = gru.DataClient(endUser);
        using var local = sjc.DataClient(endUser);
        var shape = new TableShapeClient(home, ManagementApi.Uri(database.Data).ToString(), "notes");
        await shape.SyncAsync();
        await Assert.That(shape.State.Keys.ToList()).IsEquivalentTo(AliceFirst);
        var parked = new TableShapeClient(local, ManagementApi.Uri(database.Data).ToString(), "notes").GetAsync(shape.Uri(shape.NextOffset, live: true));
        using var changed = await owner.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"), new
        {
            statements = new object[] { new { sql = "INSERT INTO notes VALUES (3, ?, 'b2', 's'), (4, ?, 'a2', 's')", args = new[] { bob, alice } } },
        }, token);
        changed.EnsureSuccessStatusCode();
        await replication.PullAllAsync(token);
        await replication.PullAllAsync(token);
        var reply = await parked.WaitAsync(TimeSpan.FromSeconds(10), token);
        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.OK).Because(System.Text.Encoding.UTF8.GetString(reply.Body));
        var ops = reply.Ops.ToList();
        await Assert.That(ops.Select(op => op.GetProperty("key").GetString()!).ToList()).IsEquivalentTo(AliceNew);
        await Assert.That(ops.Single().GetProperty("row").TryGetProperty("secret", out _)).IsFalse();
    }

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, List<RegionPeer> regions, FakeS3? backups = null, TimeProvider? time = null) => ManagementApi.StartAsync(
        settings =>
        {
            settings["region"] = region;
            backups?.Configure(settings);
        },
        services =>
        {
            if (backups is not null)
            {
                backups.Route(services);
                ManualBackupHosting.Configure(services);
            }

            services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, regions)));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, regions)));
        },
        time: time, sharedIssuer: issuer);

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }

    private sealed class RegionView(string region, List<RegionPeer> regions) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. regions.Where(peer => peer.Region != region)]);
    }

    private sealed class ReplicaView(string region, List<RegionPeer> regions) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([.. regions.Where(peer => peer.Region != region).Select(peer => new ReplicaPeer(peer.Region, peer.Region + "-1", peer.Address))]);
    }
}
