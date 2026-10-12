using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

/// <summary><c>GET /health/replication</c>, the public summary ts-status probes per region.</summary>
public sealed class ReplicationHealthTests
{
    [Test]
    public async Task A_lone_node_answers_ok_anonymously_on_the_client_listener_with_a_short_public_cache()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);

        using var response = await client.GetAsync(new Uri("/health/replication", UriKind.Relative));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.CacheControl!.ToString()).IsEqualTo("public, max-age=5");
        await Assert.That(body.RootElement.GetRawText()).IsEqualTo("""{"region":"test-region","status":"ok","maxLagSeconds":null,"peers":[]}""");
    }

    [Test]
    public async Task Each_seen_region_reports_its_lag_and_a_missing_or_slow_stream_degrades()
    {
        var status = new Streams();
        var others = new Members(["sjc", "fra"]);
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services
            .AddSingleton<IReplicationStatus>(status).AddSingleton<IMembership>(provider => others.For(provider)));
        var sjc = new ReplicationDatabaseStatus(0, "org", "project", "database", Ulid.NewUlid(), 5, 4, 1, false, 2.5, 1);

        status.State = new(true, 1, 0, [new(0, 1, [sjc], "sjc")]);
        await Assert.That(await ReadAsync(server)).IsEqualTo(
            """{"region":"test-region","status":"degraded","maxLagSeconds":2.5,"peers":[{"region":"fra","status":"degraded","lagSeconds":null},{"region":"sjc","status":"ok","lagSeconds":2.5}]}""");

        status.State = new(true, 1, 0, [new(0, 1, [sjc], "sjc"), new(1, 0, [sjc with { CatchupSeconds = 0 }], "fra")]);
        await Assert.That(await ReadAsync(server)).IsEqualTo(
            """{"region":"test-region","status":"ok","maxLagSeconds":2.5,"peers":[{"region":"fra","status":"ok","lagSeconds":0},{"region":"sjc","status":"ok","lagSeconds":2.5}]}""");

        status.State = new(true, 1, 0, [new(0, 1, [sjc with { CatchupSeconds = 45 }], "sjc"), new(1, 0, [sjc with { CatchupSeconds = 0, Diverged = true }], "fra")]);
        await Assert.That(await ReadAsync(server)).IsEqualTo(
            """{"region":"test-region","status":"degraded","maxLagSeconds":45,"peers":[{"region":"fra","status":"degraded","lagSeconds":0},{"region":"sjc","status":"degraded","lagSeconds":45}]}""");
    }

    private static async Task<string> ReadAsync(TerraBaseTestServer server)
    {
        using var response = await server.Client.GetAsync(new Uri("/health/replication", UriKind.Relative));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetRawText();
    }

    private sealed class Streams : IReplicationStatus
    {
        public ReplicationStatus State { get; set; } = new(true, 0, 0, []);

        public ReplicationStatus Snapshot() => State;
    }

    // Static membership with members in other regions, which StaticMembership never reports.
    private sealed class Members(string[] regions)
    {
        public IMembership For(IServiceProvider provider) => new View(provider.GetRequiredService<TerraBase.Configuration.TerraBaseOptions>(), regions);

        private sealed class View(TerraBase.Configuration.TerraBaseOptions options, string[] regions) : IMembership
        {
            private readonly ClusterView _view = new(1, [new(options.MachineId, string.Empty, options.Region, string.Empty)],
                [.. regions.Select((region, index) => new ClusterMember($"m{index}", string.Empty, region, string.Empty))]);

            public ClusterMember Self => _view.Members[0];

            public bool Dynamic => false;

            public bool HoldsLease => true;

            public TimeSpan Fence => TimeSpan.Zero;

            public ValueTask<ClusterView> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_view);

            public ValueTask LeaveAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }
    }
}
