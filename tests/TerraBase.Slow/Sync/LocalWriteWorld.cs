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
using TerraBase.Features.LocalAck;
using TerraBase.Features.Memory;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Features.Shapes;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Sync;

/// <summary>How the sjc node's in-region partner behaves.</summary>
internal enum LocalPartner
{
    /// <summary>A stand-in that holds every transaction.</summary>
    Held,

    /// <summary>None: sjc is a one-node region.</summary>
    Alone,

    /// <summary>A second TerraBase node, sjc-2, reached on its internal listener.</summary>
    Node,
}

/// <summary>A database homed in gru with a replica in sjc, written locally on the sjc node (R7, R10).</summary>
internal sealed class LocalWriteWorld : IAsyncDisposable
{
    private static readonly string[] ReplicaRegions = ["sjc", "fra"];

    private readonly ManagementApi _api;
    private readonly HttpClient _writer;
    private readonly JetStreamServer _nats;
    private readonly string _directory;
    private readonly ServiceProvider _services;
    private readonly HttpClient _pulls;

    private LocalWriteWorld(ManagementApi api, HttpClient writer, JetStreamServer nats, string directory, ServiceProvider services, ManagedDatabaseRef reference,
        TursoReplicas replicas, HomePush region, NatsConnection client, HttpClient pulls, IInRegionAck ack, TerraBaseTestServer? partner)
    {
        Ack = ack;
        Partner = partner;
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

    public IInRegionAck Ack { get; }

    /// <summary>sjc-2, with <see cref="LocalPartner.Node"/>.</summary>
    public TerraBaseTestServer? Partner { get; }

    /// <summary>Transactions sjc-2 holds.</summary>
    public long PartnerHolds => Partner!.Services.GetRequiredService<HoldStore>().Count;

    private bool _killed;

    public static async Task<LocalWriteWorld> StartAsync(JetStreamServer nats, LocalPartner mode = LocalPartner.Held)
    {
        var s3 = new FakeS3();
        var api = await ManagementApi.StartAsync(
            settings =>
            {
                settings["region"] = "gru";
                s3.Configure(settings);
            },
            services =>
            {
                s3.Route(services);
                services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
                services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new Peers()));
                services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new NoReplicas()));
            });
        var directory = Directory.CreateTempSubdirectory("terrabase-home-push-").FullName;
        var services = new ServiceCollection().AddSingleton(api.Server.Services.GetRequiredService<ITransactionCrypto>()).AddMetrics().AddSingleton<ILocalWriteClock>(new Admitted())
            .AddSingleton(provider => new ShapeOverlay(api.Server.Services.GetRequiredService<TerraBaseOptions>() with { NatsUrl = null }, TimeProvider.System,
                NullLogger<ShapeOverlay>.Instance))
            .AddSingleton<ILocalCommitFeed>(provider => provider.GetRequiredService<ShapeOverlay>())
            .BuildServiceProvider();
        TerraBaseTestServer? partner = null;
        try
        {
            if (mode == LocalPartner.Node)
            {
                partner = await TerraBaseTestServer.StartAsync(
                    settings =>
                    {
                        settings["region"] = "sjc";
                        s3.Configure(settings);
                    },
                    collection =>
                    {
                        s3.Route(collection);
                        collection.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
                        collection.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new Peers()));
                        collection.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new HomeAt(api.Server.BaseAddress)));
                    });
            }

            var organization = await api.CreateOrganizationAsync("Org " + EntityIds.New());
            var project = await api.CreateProjectAsync(organization, "Project");
            using var created = await api.Client.PostAsJsonAsync(
                ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main", replicaRegions = ReplicaRegions });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
            var reference = new ManagedDatabaseRef(organization, project, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!, "main");
            var writer = api.DataClient(await api.Server.IssueNarrowedAsync(organization, project, null, "sql:read", "sql:write"));
            var options = Options(api, nats, "sjc", directory);
            var replicas = new TursoReplicas(options, new TestMasterKey(), api.Server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<TursoReplicas>.Instance);
            IInRegionAck ack = mode switch
            {
                LocalPartner.Held => new Holds(healthy: true),
                _ => new InRegionAck(options, new Partners(partner?.BaseAddress), new NoReplicas(),
                    new HoldStore(api.Server.Services.GetRequiredService<IStorageEngine>(), TimeProvider.System), api.Server.Services.GetRequiredService<ITransactionCrypto>(),
                    api.Server.Services.GetRequiredService<InternalCredential>(), services.GetRequiredService<IMeterFactory>(), TimeProvider.System,
                    NullLogger<InRegionAck>.Instance),
            };
            if (ack is InRegionAck running)
            {
                await running.StartAsync(CancellationToken.None);
            }

            var region = new HomePush(options, new Peers("gru"), new Placed(), replicas, new NoApply(), new TestClock("sjc-1"), ack, services,
                services.GetRequiredService<IMeterFactory>(), TimeProvider.System, NullLogger<HomePush>.Instance);
            var client = new NatsConnection(new NatsOpts { Url = nats.Url.ToString(), AuthOpts = new NatsAuthOpts { Token = nats.Token } });
            var pulls = PeerTls.CreateClient(api.Server.BaseAddress);
            var world = new LocalWriteWorld(api, writer, nats, directory, services, reference, replicas, region, client, pulls, ack, partner);
            await world.HomeSqlAsync("CREATE TABLE items (id TEXT PRIMARY KEY, v TEXT)");
            await replicas.OpenAsync(world.Database, api.Server.PullAddress(world.Database), pulls, CancellationToken.None);
            await region.StartAsync(CancellationToken.None);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(region.Started), TimeSpan.FromSeconds(30));
            return world;
        }
        catch
        {
            await services.DisposeAsync();
            if (partner is not null)
            {
                await partner.DisposeAsync();
            }

            await api.DisposeAsync();
            throw;
        }
    }

    public async Task<Node> HomeNodeAsync()
    {
        var options = Options(_api, _nats, "gru", Path.Combine(_directory, "home-" + EntityIds.New()));
        var node = new HomePush(options, new Peers("sjc"), new Placed(), _api.Server.Services.GetRequiredService<IDatabaseReplicas>(),
            _api.Server.Services.GetRequiredService<IHomeApply>(), new TestClock("gru-1"), new Holds(healthy: true), _services, _services.GetRequiredService<IMeterFactory>(),
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

        // A commit the partner did not hold waits for the home, which some tests never start: fail instead of hanging.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        return await transaction.CommitAsync(homeDurable, bounded.Token);
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

    /// <summary>Stops the sjc node that acknowledged the writes, as if its host died: its push and its partner checks end.</summary>
    public async Task KillRegionNodeAsync()
    {
        _killed = true;
        await Region.StopAsync(CancellationToken.None);
        Region.Dispose();
        if (Ack is InRegionAck running)
        {
            await running.StopAsync(CancellationToken.None);
            running.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_killed)
        {
            await KillRegionNodeAsync();
        }

        await Replicas.DisposeAsync();
        await Client.DisposeAsync();
        _pulls.Dispose();
        _writer.Dispose();
        await _services.DisposeAsync();
        if (Partner is not null)
        {
            await Partner.DisposeAsync();
        }

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

    public sealed class Node(HomePush push) : IAsyncDisposable
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
        public ValueTask<HomeApplyResult?> ApplyAsync(TenantContext database, byte[] payload, CancellationToken cancellationToken) => ValueTask.FromResult<HomeApplyResult?>(null);

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

    private sealed class Holds(bool healthy) : IInRegionAck
    {
        public bool PartnerHealthy => healthy;

        public bool QuorumHealthy => healthy;

        public ValueTask<bool> HoldAsync(TenantContext database, string home, ulong sequence, byte[] payload, CancellationToken cancellationToken) =>
            ValueTask.FromResult(healthy);

        public void Release(TenantContext database, ulong through)
        {
        }
    }

    private sealed class Partners(Uri? partner) : IRegionPartners
    {
        public ValueTask<IReadOnlyList<RegionPartner>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPartner>>(partner is null ? [] : [new RegionPartner("sjc-2", partner)]);
    }

    private sealed class HomeAt(Uri home) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([new ReplicaPeer("gru", "gru-1", home)]);
    }
}
