using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Configuration;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Sync;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class RegionShapeStreamTests
{
    [Test]
    public async Task Sjc_changes_reach_fra_before_home_apply_then_confirm_or_revert_without_duplicates()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await LocalWriteWorld.StartAsync(nats);
        await using var fra = await Reader.StartAsync(world, nats, token);
        await world.Overlay.StartAsync(token);
        await world.Overlay.Subscribed.WaitAsync(TimeSpan.FromSeconds(5), token);

        var parked = fra.NextAsync(token);
        var commit = await world.LocalAsync(false, ["INSERT INTO items (id, v) VALUES ('p', 'sjc')"], token);
        var provisional = await parked.WaitAsync(TimeSpan.FromSeconds(5), token);
        await Assert.That(Pending(provisional).Single().GetProperty("id").GetString()).IsEqualTo(commit.Id);
        await Assert.That(Pending(provisional).Single().GetProperty("ops")[0].GetProperty("row").GetProperty("v").GetString()).IsEqualTo("sjc");
        await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM items")).GetRawText()).IsEqualTo("[[0]]");
        var duplicate = world.Overlay.Snapshot(world.Database).Single();
        fra.Overlay.Add(world.Database, duplicate);
        await Assert.That(fra.Overlay.Snapshot(world.Database).Count).IsEqualTo(1);

        await using (var home = await world.HomeNodeAsync())
        {
            await TerraBaseTestServer.EventuallyAsync(async _ => (await world.HomeSqlAsync("SELECT count(*) FROM items")).GetRawText() == "[[1]]", TimeSpan.FromSeconds(10));
        }

        await fra.PullAsync(token);
        var confirmed = await fra.NextAsync(token);
        await Assert.That(Settled(confirmed, "confirmed")).IsEquivalentTo([commit.Id]);
        await Assert.That(Pending(confirmed).Count).IsEqualTo(0);
        // A delayed direct message after the pages arrived must never resurrect the provisional copy.
        fra.Overlay.Add(world.Database, duplicate);
        fra.Local = "1111111111111111";
        await Assert.That(Pending(await fra.NextAsync(token)).Count).IsEqualTo(0);

        await world.HomeSqlAsync("INSERT INTO items (id, v) VALUES ('x', 'gru')");
        commit = await world.LocalAsync(false, ["INSERT INTO items (id, v) VALUES ('x', 'sjc')"], token);
        provisional = await fra.NextAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
        await Assert.That(Pending(provisional).Single().GetProperty("id").GetString()).IsEqualTo(commit.Id);
        await using (var home = await world.HomeNodeAsync())
        {
            await TerraBaseTestServer.EventuallyAsync(async _ => (await world.HomeSqlAsync("SELECT count(*) FROM terrascale.conflicts")).GetRawText() == "[[1]]", TimeSpan.FromSeconds(10));
        }

        await fra.PullAsync(token);
        var reverted = await fra.NextAsync(token);
        await Assert.That(Settled(reverted, "revert")).IsEquivalentTo([commit.Id]);
        await Assert.That(Pending(reverted).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Cutting_only_the_direct_fra_link_keeps_the_home_path_and_reconnect_does_not_duplicate_rows()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await LocalWriteWorld.StartAsync(nats);
        await using var link = new Link(nats.Url);
        await using var fra = await Reader.StartAsync(world, nats, token, link.Url);
        await world.Overlay.StartAsync(token);
        await world.Overlay.Subscribed.WaitAsync(TimeSpan.FromSeconds(5), token);
        link.Cut();
        await using var home = await world.HomeNodeAsync();
        var commit = await world.LocalAsync(true, ["INSERT INTO items (id, v) VALUES ('p', 'sjc')"], token);
        await Assert.That(fra.Overlay.Snapshot(world.Database).Count).IsEqualTo(0);
        await fra.PullAsync(token);
        var confirmed = await fra.NextAsync(token);
        await Assert.That(Settled(confirmed, "confirmed")).IsEquivalentTo([commit.Id]);
        await Assert.That(Pending(confirmed).Count).IsEqualTo(0);
        var ops = confirmed.GetProperty("changes").EnumerateArray().SelectMany(c => c.GetProperty("ops").EnumerateArray()).ToList();
        await Assert.That(ops.Count).IsEqualTo(1);
        await Assert.That(ops[0].GetProperty("row").GetProperty("v").GetString()).IsEqualTo("sjc");
        link.Restore();
        // The same ID through a late direct delivery is filtered against the home pages.
        fra.Overlay.Add(world.Database, world.Overlay.Snapshot(world.Database).Single());
        fra.Local = "1111111111111111";
        var replay = await fra.NextAsync(token);
        await Assert.That(Pending(replay).Count).IsEqualTo(0);
        await Assert.That(replay.GetProperty("changes").GetArrayLength()).IsEqualTo(0);
        await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM items")).GetRawText()).IsEqualTo("[[1]]");
    }

    private static List<JsonElement> Pending(JsonElement body) => body.GetProperty("provisional").EnumerateArray().ToList();

    private static List<string?> Settled(JsonElement body, string state) => body.GetProperty("changes").EnumerateArray()
        .SelectMany(change => change.TryGetProperty(state, out var ids) ? ids.EnumerateArray().Select(id => id.GetString()) : []).ToList();

    private sealed class Reader(LocalWriteWorld world, TursoReplicas replicas, ShapeOverlay overlay, ReplicaShapeTails tails, ResolvedShape shape,
        HttpClient pulls, string directory, long offset) : IAsyncDisposable
    {
        public ShapeOverlay Overlay => overlay;

        public string Local { get; set; } = ShapeProvisional.Empty;

        private long _offset = offset;

        public static async Task<Reader> StartAsync(LocalWriteWorld world, JetStreamServer nats, CancellationToken token, Uri? link = null)
        {
            _ = await world.LocalAsync(false, [], token);
            var client = new TableShapeClient(world.Writer, ManagementApi.Uri(world.Reference.Data).ToString(), "items");
            _ = await client.SyncAsync();
            var services = world.Api.Server.Services;
            var directory = Directory.CreateTempSubdirectory("terrabase-fra-shape-").FullName;
            var options = services.GetRequiredService<TerraBaseOptions>() with
            {
                Region = "fra", MachineId = "fra-1", DataDirectory = directory, NatsUrl = link ?? nats.Url, NatsToken = nats.Token,
            };
            var replicas = new TursoReplicas(options, new TestMasterKey(), services.GetRequiredService<IMemoryBudget>(), NullLogger<TursoReplicas>.Instance);
            var overlay = new ShapeOverlay(options, TimeProvider.System, NullLogger<ShapeOverlay>.Instance);
            var pulls = PeerTls.CreateClient(world.Api.Server.BaseAddress);
            try
            {
                await replicas.OpenAsync(world.Database, world.Api.Server.PullAddress(world.Database), pulls, token);
                await overlay.StartAsync(token);
                await overlay.Subscribed.WaitAsync(TimeSpan.FromSeconds(5), token);
                var tails = new ReplicaShapeTails(replicas, services.GetRequiredService<IMemoryBudget>(), TimeProvider.System,
                    services.GetRequiredService<IShapeFlights<ShapeRead>>(), services.GetRequiredService<ShapeControls>(), overlay);
                var shape = await tails.ResolveAsync(world.Database, "items", new ShapeQuery(null, null), token);
                var resolved = shape is ResolvedShape found ? found : throw new InvalidOperationException("fra did not resolve the shape");
                var offset = long.Parse(client.NextOffset, System.Globalization.CultureInfo.InvariantCulture);
                await using var reader = (await replicas.BeginShapeReadAsync(world.Database, token))!;
                while (await reader.ReadAsync(resolved.Scope, resolved.Lineage, offset, services.GetRequiredService<IMemoryBudget>(), token) is ShapeBody body)
                {
                    body.Lease?.Dispose();
                    offset = long.Parse(body.NextOffset, System.Globalization.CultureInfo.InvariantCulture);
                    if (body.UpToDate)
                    {
                        break;
                    }
                }

                return new Reader(world, replicas, overlay, tails, resolved, pulls, directory, offset);
            }
            catch
            {
                await overlay.StopAsync(CancellationToken.None);
                overlay.Dispose();
                await replicas.DisposeAsync();
                pulls.Dispose();
                Directory.Delete(directory, true);
                throw;
            }
        }

        public async Task<JsonElement> NextAsync(CancellationToken token)
        {
            var read = await tails.WaitAsync(shape.Scope, shape.Lineage, _offset, Local, token);
            if (read is not ShapeBody body)
            {
                throw new InvalidOperationException("the fra tail failed: " + read);
            }

            using (body.Lease)
            {
                var json = JsonElement.Parse(body.Json.Span);
                _offset = long.Parse(json.GetProperty("next_offset").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                Local = json.GetProperty("local").GetString()!;
                return json;
            }
        }

        public async Task PullAsync(CancellationToken token) => _ = await replicas.PullAsync(world.Database, true, token);

        public async ValueTask DisposeAsync()
        {
            await overlay.StopAsync(CancellationToken.None);
            overlay.Dispose();
            tails.Dispose();
            await replicas.DisposeAsync();
            pulls.Dispose();
            Directory.Delete(directory, true);
        }
    }

    // An owned TCP link for only the fra provisional connection. R7 and HTTP page pulls bypass it.
    private sealed class Link : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<TcpClient, TcpClient> _connections = new();
        private readonly Task _accept;
        private readonly Uri _target;
        private volatile bool _cut;

        public Link(Uri target)
        {
            _target = target;
            _listener.Start();
            Url = new Uri("nats://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port);
            _accept = AcceptAsync();
        }

        public Uri Url { get; }

        public void Cut()
        {
            _cut = true;
            foreach (var (client, peer) in _connections)
            {
                client.Dispose();
                peer.Dispose();
            }
        }

        public void Restore() => _cut = false;

        private async Task AcceptAsync()
        {
            var tasks = new List<Task>();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    tasks.RemoveAll(task => task.IsCompletedSuccessfully);
                    tasks.Add(RelayAsync(client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            finally
            {
                await Task.WhenAll(tasks);
            }
        }

        private async Task RelayAsync(TcpClient client)
        {
            using (client)
            using (var peer = new TcpClient())
            {
                if (_cut)
                {
                    return;
                }

                _connections[client] = peer;
                try
                {
                    await peer.ConnectAsync(_target.Host, _target.Port, _stop.Token);
                    var outgoing = client.GetStream().CopyToAsync(peer.GetStream(), _stop.Token);
                    var incoming = peer.GetStream().CopyToAsync(client.GetStream(), _stop.Token);
                    _ = await Task.WhenAny(outgoing, incoming);
                    client.Dispose();
                    peer.Dispose();
                    await Task.WhenAll(outgoing, incoming);
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
                finally
                {
                    _connections.TryRemove(client, out _);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            Cut();
            await _accept;
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
