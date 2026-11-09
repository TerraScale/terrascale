using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Features.Clock;
using TerraBase.Features.HomePush;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Sync;

public sealed class RevisionNoticeReplayTests
{
    private const string Schema = "CREATE TABLE items (id TEXT PRIMARY KEY, v TEXT)";
    private static readonly string[] ReplicaRegions = ["sjc"];

    [Test]
    public async Task A_write_acknowledged_while_the_home_NATS_connection_is_down_reaches_the_replica_on_reconnect(CancellationToken token)
    {
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var homeNats = new RegionalNatsFault(nats.Url);
        await using var replicaNats = new RegionalNatsFault(nats.Url);
        await using var cluster = await Cluster.StartAsync(nats, homeNats, replicaNats, token);
        await cluster.CreateDatabaseAsync(token);
        var (seeded, seedBody) = await cluster.InsertAsync("one", token);
        await Assert.That(seeded).IsEqualTo(HttpStatusCode.OK).Because(seedBody);
        await cluster.WaitUntilVisibleAsync("one", TimeSpan.FromSeconds(30), token);

        var dropped = homeNats.Drop((subject, _) => subject.StartsWith("terrabase.revision.", StringComparison.Ordinal));
        var (status, body) = await cluster.InsertAsync("two", token);
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
        await dropped.WaitAsync(TimeSpan.FromSeconds(5), token);
        homeNats.Allow();
        homeNats.Partition();
        homeNats.Heal();

        await cluster.WaitUntilVisibleAsync("two", TimeSpan.FromSeconds(5), token);
    }

    private sealed class Cluster : IAsyncDisposable
    {
        private readonly TestIssuer _issuer;
        private readonly ManagementApi _gru;
        private readonly ManagementApi _sjc;
        private readonly HttpClient _manage;
        private readonly HttpClient _viaGru;
        private readonly HttpClient _viaSjc;
        private readonly string _project;

        private Cluster(TestIssuer issuer, ManagementApi gru, ManagementApi sjc, HttpClient manage, HttpClient viaGru, HttpClient viaSjc, string project)
        {
            (_issuer, _gru, _sjc, _manage, _viaGru, _viaSjc, _project) = (issuer, gru, sjc, manage, viaGru, viaSjc, project);
        }

        public static async Task<Cluster> StartAsync(JetStreamServer nats, RegionalNatsFault homeNats, RegionalNatsFault replicaNats, CancellationToken token)
        {
            var issuer = new TestIssuer();
            var keys = new FakeS3();
            var regions = new ConcurrentDictionary<string, Uri>(StringComparer.Ordinal);
            var gru = await StartAsync("gru", issuer, regions, nats, homeNats.Url, keys);
            regions["gru"] = gru.Server.BaseAddress;
            var sjc = await StartAsync("sjc", issuer, regions, nats, replicaNats.Url, keys);
            regions["sjc"] = sjc.Server.BaseAddress;
            var organization = await gru.CreateOrganizationAsync("Org " + EntityIds.New());
            await gru.SetPlanAsync(gru.User, organization, "enterprise", token);
            var project = await gru.CreateProjectAsync(organization, "Project");
            var bearer = issuer.Mint(gru.User, descriptor =>
            {
                descriptor.Expires = DateTime.UtcNow.AddMinutes(30);
                descriptor.Claims["project_id"] = project;
                descriptor.Claims["scope"] = "databases:read sql:read sql:write";
            });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(
                sjc.Server.Services.GetRequiredService<HomePush>().Started && gru.Server.Services.GetRequiredService<HomePush>().Started), TimeSpan.FromSeconds(30));
            return new Cluster(issuer, gru, sjc, gru.ProjectClient(project), gru.WithToken(bearer), sjc.WithToken(bearer), project);
        }

        public async Task CreateDatabaseAsync(CancellationToken token)
        {
            using var created = await _manage.PostAsJsonAsync(
                ManagementApi.Uri($"/v1/projects/{_project}/databases"), new { name = "replay", replicaRegions = ReplicaRegions }, token);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
            var (status, body) = await ExecuteAsync(_viaGru, Schema, token);
            await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
            var replication = _sjc.Server.Services.GetRequiredService<RegionReplication>();
            await replication.SyncCatalogsAsync(token);
            await replication.PullAllAsync(token);
        }

        public async Task<(HttpStatusCode Status, string Body)> InsertAsync(string id, CancellationToken token) =>
            await ExecuteAsync(_viaGru, $"INSERT INTO items (id, v) VALUES ('{id}', '{id}')", token);

        public async Task WaitUntilVisibleAsync(string id, TimeSpan bound, CancellationToken token) =>
            await TerraBaseTestServer.EventuallyAsync(async cancellation =>
            {
                var (status, body) = await ExecuteAsync(_viaSjc, "SELECT id FROM items ORDER BY id", cancellation);
                return status == HttpStatusCode.OK && body.Contains(id);
            }, bound);

        private async Task<(HttpStatusCode Status, string Body)> ExecuteAsync(HttpClient client, string sql, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ManagementApi.Uri($"/v1/projects/{_project}/databases/replay/sql/execute"))
            {
                Content = JsonContent.Create(new { statements = new[] { new { sql } } }),
            };
            using var response = await client.SendAsync(request, token);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(token));
        }

        public async ValueTask DisposeAsync()
        {
            _viaGru.Dispose();
            _viaSjc.Dispose();
            await _sjc.DisposeAsync();
            await _gru.DisposeAsync();
            _issuer.Dispose();
        }

        private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, ConcurrentDictionary<string, Uri> regions, JetStreamServer nats, Uri natsUrl, FakeS3 keys) =>
            ManagementApi.StartAsync(
                settings =>
                {
                    settings["region"] = region;
                    keys.Configure(settings);
                },
                services =>
                {
                    keys.Route(services);
                    var registered = services.Single(service => service.ServiceType == typeof(TerraBaseOptions));
                    services[services.IndexOf(registered)] = ServiceDescriptor.Singleton(((TerraBaseOptions)registered.ImplementationInstance!) with
                    {
                        MachineId = region + "-1",
                        NatsUrl = natsUrl,
                        NatsToken = nats.Token,
                    });
                    services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, regions)));
                    services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
                    services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, regions)));
                    services.Replace(ServiceDescriptor.Singleton<ILocalWriteClock>(new Admitted()));
                },
                sharedIssuer: issuer);
    }

    private sealed class Admitted : ILocalWriteClock
    {
        public bool Allowed => true;

        public double SkewMilliseconds => 0;
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }

    private sealed class RegionView(string region, ConcurrentDictionary<string, Uri> regions) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. regions.Where(peer => peer.Key != region).Select(peer => new RegionPeer(peer.Key, peer.Value))]);
    }

    private sealed class ReplicaView(string region, ConcurrentDictionary<string, Uri> regions) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([.. regions.Where(peer => peer.Key != region).Select(peer => new ReplicaPeer(peer.Key, peer.Key + "-1", peer.Value))]);
    }
}
