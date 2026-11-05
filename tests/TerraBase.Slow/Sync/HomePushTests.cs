using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Clock;
using TerraBase.Features.HomeApply;
using TerraBase.Features.HomePush;
using TerraBase.Features.Memory;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Sync;

public sealed class HomePushTests
{
    private static readonly string[] ReplicaRegions = ["sjc", "fra"];

    [Test]
    public async Task A_local_write_in_sjc_is_visible_at_once_and_durability_home_answers_after_gru_applied_it()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await World.StartAsync(nats);
        await using var home = await world.HomeNodeAsync();

        var committed = await world.LocalAsync(homeDurable: true,
            ["INSERT INTO items (id, v) VALUES ('a', 'sjc-1')", "INSERT INTO items (id, v) VALUES ('b', 'sjc-1')", "UPDATE items SET v = 'sjc-2' WHERE id = 'a'"], token);

        await Assert.That(committed.Home!.State).IsEqualTo(WriteStates.Confirmed);
        await Assert.That((await world.HomeSqlAsync("SELECT id, v FROM items ORDER BY id")).GetRawText()).IsEqualTo("""[["a","sjc-2"],["b","sjc-1"]]""");
        await Assert.That(await world.RegionRowsAsync(token)).IsEqualTo("a=sjc-2,b=sjc-1");

        await world.HomeSqlAsync("INSERT INTO items (id, v) VALUES ('h', 'gru')");
        _ = await world.Replicas.PullAsync(world.Database, fromHome: true, token);
        await Assert.That(await world.RegionRowsAsync(token)).IsEqualTo("a=sjc-2,b=sjc-1,h=gru");
        await Assert.That(world.Region.Overlay.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_cut_link_loses_nothing_a_pull_while_pending_applies_nothing_twice_and_redelivery_applies_once()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await World.StartAsync(nats);
        await world.HomeSqlAsync("CREATE TABLE counts (id TEXT PRIMARY KEY, n INTEGER NOT NULL)");
        await world.HomeSqlAsync("CREATE TABLE audit (id TEXT PRIMARY KEY, what TEXT)");
        await world.HomeSqlAsync("CREATE TRIGGER counts_audit AFTER INSERT ON counts BEGIN INSERT INTO audit (id, what) VALUES (new.id || '-audit', 'insert'); END");
        _ = await world.Replicas.PullAsync(world.Database, fromHome: true, token);

        var pending = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            pending.Add((await world.LocalAsync(homeDurable: false, [$"INSERT INTO counts (id, n) VALUES ('c{i}', {i})"], token)).Id!);
        }

        for (var round = 0; round < 3; round++)
        {
            await world.HomeSqlAsync($"INSERT INTO items (id, v) VALUES ('home-{round}', 'gru')");
            await Assert.That((await world.Replicas.PullAsync(world.Database, fromHome: true, token))!.Changed).IsTrue();
            await Assert.That(await world.RegionScalarAsync("SELECT count(*) || '/' || sum(n) FROM counts", token)).IsEqualTo("5/10").Because($"pull {round} while the home is cut off");
            await Assert.That(await world.RegionScalarAsync("SELECT count(*) FROM audit", token)).IsEqualTo("5").Because($"pull {round}: re-applying fires no trigger");
        }

        await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM counts")).GetRawText()).IsEqualTo("[[0]]");

        await using (var home = await world.HomeNodeAsync())
        {
            await TerraBaseTestServer.EventuallyAsync(async cancellation =>
                (await world.HomeSqlAsync("SELECT count(*) || '/' || sum(n) FROM counts")).GetRawText() == """[["5/10"]]""", TimeSpan.FromSeconds(30));
            await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM audit")).GetRawText()).IsEqualTo("[[5]]");
        }

        await using (var restarted = await world.HomeNodeAsync())
        {
            var js = new NatsJSContext(world.Client);
            await Assert.That(await restarted.Push.ReplayFromAsync("sjc", world.Database, pending[0], token)).IsEqualTo(2UL);
            await TerraBaseTestServer.EventuallyAsync(async cancellation =>
                (await js.GetConsumerAsync(PushNames.Stream("sjc"), PushNames.Consumer("gru"), cancellation)).Info.NumAckPending == 0
                && (await js.GetConsumerAsync(PushNames.Stream("sjc"), PushNames.Consumer("gru"), cancellation)).Info.NumPending == 0, TimeSpan.FromSeconds(30));
            var again = await world.Region.PublishAgainAsync(world.Database, pending[0], token);
            await Assert.That(again.Duplicate).IsTrue();
        }

        await Assert.That((await world.HomeSqlAsync("SELECT count(*) || '/' || sum(n) FROM counts")).GetRawText()).IsEqualTo("""[["5/10"]]""");
        await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM audit")).GetRawText()).IsEqualTo("[[5]]");
        await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM terrascale.conflicts")).GetRawText()).IsEqualTo("[[0]]");

        await world.HomeSqlAsync("UPDATE counts SET n = 100 WHERE id = 'c0'");
        _ = await world.Replicas.PullAsync(world.Database, fromHome: true, token);
        await Assert.That(await world.RegionScalarAsync("SELECT count(*) || '/' || sum(n) FROM counts", token)).IsEqualTo("5/110");
        await Assert.That(world.Region.Overlay.Count).IsEqualTo(0);
    }

    internal sealed class World : IAsyncDisposable
    {
        private readonly ManagementApi _api;
        private readonly HttpClient _writer;
        private readonly JetStreamServer _nats;
        private readonly string _directory;
        private readonly ServiceProvider _services;
        private readonly HttpClient _pulls;

        private World(ManagementApi api, HttpClient writer, JetStreamServer nats, string directory, ServiceProvider services, ManagedDatabaseRef reference,
            TursoReplicas replicas, HomePush region, NatsConnection client, HttpClient pulls)
        {
            _pulls = pulls;
            _api = api;
            _writer = writer;
            _nats = nats;
            _directory = directory;
            _services = services;
            Reference = reference;
            Database = new TenantContext(reference.Organization, PlanTier.Hobby, reference.Project, reference.Id);
            Replicas = replicas;
            Region = region;
            Client = client;
        }

        public ManagementApi Api => _api;

        public HttpClient Writer => _writer;

        public ShapeOverlay Overlay => _services.GetRequiredService<ShapeOverlay>();

        public ManagedDatabaseRef Reference { get; }

        public TenantContext Database { get; }

        public TursoReplicas Replicas { get; }

        public HomePush Region { get; }

        public NatsConnection Client { get; }

        public static async Task<World> StartAsync(JetStreamServer nats)
        {
            var api = await ManagementApi.StartAsync(
                settings => settings["region"] = "gru",
                services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
                    services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new Peers()));
                    services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new NoReplicas()));
                });
            var directory = Directory.CreateTempSubdirectory("terrabase-home-push-").FullName;
            var services = new ServiceCollection().AddMetrics().AddSingleton<ILocalWriteClock>(new Admitted())
                .AddSingleton(provider => new ShapeOverlay(api.Server.Services.GetRequiredService<TerraBaseOptions>() with { NatsUrl = null }, TimeProvider.System,
                    NullLogger<ShapeOverlay>.Instance))
                .AddSingleton<ILocalCommitFeed>(provider => provider.GetRequiredService<ShapeOverlay>())
                .BuildServiceProvider();
            try
            {
                var organization = await api.CreateOrganizationAsync("Org " + EntityIds.New());
                var project = await api.CreateProjectAsync(organization, "Project");
                using var created = await api.Client.PostAsJsonAsync(
                    ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main", replicaRegions = ReplicaRegions });
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
                var reference = new ManagedDatabaseRef(organization, project, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!, "main");
                var writer = api.DataClient(await api.Server.IssueNarrowedAsync(organization, project, null, "sql:read", "sql:write"));
                var options = Options(api, nats, "sjc", directory);
                var replicas = new TursoReplicas(options, new TestMasterKey(), api.Server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<TursoReplicas>.Instance);
                var region = new HomePush(options, new Peers("gru"), new Placed(), replicas, new NoApply(), new TestClock("sjc-1"), services,
                    services.GetRequiredService<IMeterFactory>(), TimeProvider.System, NullLogger<HomePush>.Instance);
                var client = new NatsConnection(new NatsOpts { Url = nats.Url.ToString(), AuthOpts = new NatsAuthOpts { Token = nats.Token } });
                var pulls = PeerTls.CreateClient(api.Server.BaseAddress);
                var world = new World(api, writer, nats, directory, services, reference, replicas, region, client, pulls);
                await world.HomeSqlAsync("CREATE TABLE items (id TEXT PRIMARY KEY, v TEXT)");
                await replicas.OpenAsync(world.Database, api.Server.PullAddress(world.Database), pulls, CancellationToken.None);
                await region.StartAsync(CancellationToken.None);
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(region.Started), TimeSpan.FromSeconds(30));
                return world;
            }
            catch
            {
                await services.DisposeAsync();
                await api.DisposeAsync();
                throw;
            }
        }

        public async Task<Node> HomeNodeAsync()
        {
            var options = Options(_api, _nats, "gru", Path.Combine(_directory, "home-" + EntityIds.New()));
            var node = new HomePush(options, new Peers("sjc"), new Placed(), _api.Server.Services.GetRequiredService<IDatabaseReplicas>(),
                _api.Server.Services.GetRequiredService<IHomeApply>(), new TestClock("gru-1"), _services, _services.GetRequiredService<IMeterFactory>(),
                TimeProvider.System, NullLogger<HomePush>.Instance);
            await node.StartAsync(CancellationToken.None);
            return new Node(node);
        }

        public async Task<LocalCommit> LocalAsync(bool homeDurable, IReadOnlyList<string> statements, CancellationToken cancellationToken)
        {
            await using var transaction = (await Region.BeginAsync(Database, cancellationToken))!;
            foreach (var sql in statements)
            {
                _ = await transaction.ExecuteAsync(ApplySql.Statement(sql), cancellationToken);
            }

            return await transaction.CommitAsync(homeDurable, cancellationToken);
        }

        public async Task<string> RegionRowsAsync(CancellationToken cancellationToken)
        {
            await using var reader = (await Replicas.BeginReadAsync(Database, cancellationToken))!;
            var rows = await reader.ExecuteAsync(ApplySql.Statement("SELECT id, v FROM items ORDER BY id"), cancellationToken);
            return string.Join(",", rows.Rows.Select(row => $"{row[0]}={row[1]}"));
        }

        public async Task<string?> RegionScalarAsync(string sql, CancellationToken cancellationToken)
        {
            await using var reader = (await Replicas.BeginReadAsync(Database, cancellationToken))!;
            return Convert.ToString((await reader.ExecuteAsync(ApplySql.Statement(sql), cancellationToken)).Rows.Single()[0], System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<JsonElement> HomeSqlAsync(string sql)
        {
            using var response = await _writer.PostAsJsonAsync(ManagementApi.Uri(Reference.Data + "/sql/execute"), new { statements = new[] { new { sql } } });
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because($"{sql}: {body}");
            return JsonElement.Parse(body).GetProperty("results")[0].GetProperty("rows");
        }

        public async ValueTask DisposeAsync()
        {
            await Region.StopAsync(CancellationToken.None);
            Region.Dispose();
            await Replicas.DisposeAsync();
            await Client.DisposeAsync();
            _pulls.Dispose();
            _writer.Dispose();
            await _services.DisposeAsync();
            await _api.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }

        private static TerraBaseOptions Options(ManagementApi api, JetStreamServer nats, string region, string directory) =>
            api.Server.Services.GetRequiredService<TerraBaseOptions>() with
            {
                Region = region,
                MachineId = region + "-1",
                DataDirectory = directory,
                NatsUrl = nats.Url,
                NatsToken = nats.Token,
            };
    }

    internal sealed class Node(HomePush push) : IAsyncDisposable
    {
        public HomePush Push => push;

        public async ValueTask DisposeAsync()
        {
            await push.StopAsync(CancellationToken.None);
            push.Dispose();
        }
    }

    private sealed class TestClock(string node) : IHlcClock
    {
        private readonly Lock _lock = new();
        private HlcTimestamp _last = new(0, 0, node);

        public ValueTask<HlcTimestamp> NextAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _last = now > _last.PhysicalMilliseconds ? new HlcTimestamp(now, 0, node) : _last with { Logical = _last.Logical + 1 };
                return ValueTask.FromResult(_last);
            }
        }

        public ValueTask<HlcTimestamp> ObserveAsync(HlcTimestamp remote, CancellationToken cancellationToken) => NextAsync(cancellationToken);
    }

    private sealed class Admitted : ILocalWriteClock
    {
        public bool Allowed => true;

        public double SkewMilliseconds => 0;
    }

    private sealed class Placed : IReplicaCatalog
    {
        public ValueTask<DatabaseRecordResult> FindAsync(TenantContext database, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DatabaseRecordResult>(new TenantDatabaseRecord("main", new DatabasePlacement("gru", ["sjc", "fra"], 1)));

        public ValueTask<ReadDatabaseRecordsResult> ListHomedAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<bool> RecordAsync(ReplicaCatalogEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask UpdatePlacementAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ForgetAsync(TenantContext database, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoApply : IHomeApply
    {
        public ValueTask<HomeApplyResult?> ApplyAsync(TenantContext database, RegionTransaction transaction, CancellationToken cancellationToken) => ValueTask.FromResult<HomeApplyResult?>(null);

        public ValueTask<HomeApplyResult?> StateAsync(TenantContext database, string transactionId, CancellationToken cancellationToken) => ValueTask.FromResult<HomeApplyResult?>(null);

        public ValueTask PrepareAsync(TenantContext database, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StampHomeWritesAsync(IStorageTransaction transaction, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc", "fra"]);
    }

    private sealed class Peers(params string[] regions) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. regions.Select(region => new RegionPeer(region, new Uri($"https://{region}.peer.test")))]);
    }

    private sealed class NoReplicas : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([]);
    }
}
