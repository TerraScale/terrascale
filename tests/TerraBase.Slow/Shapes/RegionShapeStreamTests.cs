using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.JetStream;
using TerraBase.Configuration;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Sync;
using TerraBase.Features.Backup;
using TerraBase.Features.HomePush;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;

namespace TerraBase.Fast.Shapes;

public sealed class RegionShapeStreamTests
{
    [Test]
    public async Task Sjc_changes_reach_fra_before_home_apply_then_confirm_or_revert_without_duplicates()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await LocalWriteWorld.StartAsync(nats);
        await world.Overlay.StartAsync(token);
        await world.Overlay.Ready.WaitAsync(TimeSpan.FromSeconds(5), token);
        await using var wire = await world.Client.SubscribeCoreAsync<byte[]>("terrabase.provisional.sjc.>", cancellationToken: token);
        _ = await world.Client.PingAsync(token);
        // An older node's raw local notices must never enter the sealed provisional stream during a rollout.
        await world.Client.PublishAsync($"terrabase.local.sjc.{world.Database.OrganizationId}.{world.Database.ProjectId}.{world.Database.DatabaseId}", "raw local notice"u8.ToArray(), cancellationToken: token);
        // fra joins after the original insert. Home assigns the same rowid to a different primary key.
        var inserted = await world.LocalAsync(false, ["INSERT INTO items (id, v) VALUES ('p', 'first')"], token);
        var encrypted = await wire.Msgs.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
        await Assert.That(encrypted.Data!.AsSpan(0, 4).SequenceEqual("TBP1"u8)).IsTrue();
        var js = new NatsJSContext(world.Client);
        await TerraBaseTestServer.EventuallyAsync(async cancellation =>
            (await js.GetStreamAsync("TERRABASE_PROVISIONAL_SJC", cancellationToken: cancellation)).Info.State.Messages == 1, TimeSpan.FromSeconds(5));
        await world.HomeSqlAsync("INSERT INTO items (id, v) VALUES ('h', 'gru')");
        await using var fra = await Reader.StartAsync(world, nats, token);
        var parked = fra.NextAsync(token);
        var commit = await world.LocalAsync(false, ["UPDATE items SET v = 'sjc' WHERE id = 'p'"], token);
        var provisional = await parked.WaitAsync(TimeSpan.FromSeconds(5), token);
        await Assert.That(Pending(provisional).Single().GetProperty("id").GetString()).IsEqualTo(commit.Id);
        await Assert.That(Pending(provisional).Single().GetProperty("ops")[0].GetProperty("row").GetProperty("v").GetString()).IsEqualTo("sjc");
        await Assert.That(Pending(provisional).Single().GetProperty("ops")[0].GetProperty("key").GetString()!).StartsWith("~" + commit.Id + ".");
        await Assert.That((await world.HomeSqlAsync("SELECT id FROM items")).GetRawText()).IsEqualTo("[[\"h\"]]");
        var duplicate = world.Overlay.Snapshot(world.Database).Single(entry => entry.Id == commit.Id);
        fra.Overlay.Add(world.Database, duplicate);
        await Assert.That(fra.Overlay.Snapshot(world.Database).Count).IsEqualTo(1);

        await using (var home = await world.HomeNodeAsync())
        {
            await TerraBaseTestServer.EventuallyAsync(async _ => (await world.HomeSqlAsync("SELECT v FROM items WHERE id = 'p'")).GetRawText() == "[[\"sjc\"]]", TimeSpan.FromSeconds(10));
        }

        await fra.PullAsync(token);
        var confirmed = await fra.NextAsync(token);
        await Assert.That(Settled(confirmed, "confirmed")).IsEquivalentTo([inserted.Id, commit.Id]);
        await Assert.That(Pending(confirmed).Count).IsEqualTo(0);
        // A delayed direct message after the pages arrived must never resurrect the provisional copy.
        await DeliverAsync(world, fra, duplicate, token);
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
        var winner = reverted.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray())
            .Single(op => op.GetProperty("row").GetProperty("id").GetString() == "x");
        await Assert.That(winner.GetProperty("row").GetProperty("v").GetString()).IsEqualTo("gru");
        await Assert.That(await world.Api.Server.Services.GetRequiredService<IBackupKeyShredding>().DestroyAsync(world.Database, token)).IsTrue();
        await Assert.That(await world.Api.Server.Services.GetRequiredService<ITransactionCrypto>().OpenAsync(world.Database, encrypted.Data!, token)).IsNull();
        await world.Overlay.PurgeDeletedAsync(world.Database, token);
        await Assert.That((await js.GetStreamAsync("TERRABASE_PROVISIONAL_SJC", cancellationToken: token)).Info.State.Messages).IsEqualTo(0L);
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
        await world.Overlay.Ready.WaitAsync(TimeSpan.FromSeconds(5), token);
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
        await DeliverAsync(world, fra, world.Overlay.Snapshot(world.Database).Single(), token);
        fra.Local = "1111111111111111";
        var replay = await fra.NextAsync(token);
        await Assert.That(Pending(replay).Count).IsEqualTo(0);
        await Assert.That(replay.GetProperty("changes").GetArrayLength()).IsEqualTo(0);
        await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM items")).GetRawText()).IsEqualTo("[[1]]");
    }

    private static async Task DeliverAsync(LocalWriteWorld world, Reader fra, ProvisionalTransaction transaction, CancellationToken token)
    {
        var database = world.Database;
        var payload = JsonSerializer.SerializeToUtf8Bytes(transaction, ShapeOverlayJsonContext.Default.ProvisionalTransaction);
        payload = await world.Api.Server.Services.GetRequiredService<ITransactionCrypto>().SealAsync(database, payload, token);
        await TerraBaseTestServer.EventuallyAsync(async _ =>
        {
            await world.Client.PublishAsync($"terrabase.provisional.sjc.{database.OrganizationId}.{database.ProjectId}.{database.DatabaseId}", payload, cancellationToken: token);
            return fra.Overlay.Snapshot(database).Any(entry => entry.Id == transaction.Id);
        }, TimeSpan.FromSeconds(5));
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
                Region = "fra",
                MachineId = "fra-1",
                DataDirectory = directory,
                NatsUrl = link ?? nats.Url,
                NatsToken = nats.Token,
            };
            var replicas = new TursoReplicas(options, new TestMasterKey(), services.GetRequiredService<IMemoryBudget>(), NullLogger<TursoReplicas>.Instance);
            var overlay = new ShapeOverlay(options, TimeProvider.System, services.GetRequiredService<ITransactionCrypto>(), NullLogger<ShapeOverlay>.Instance);
            var pulls = PeerTls.CreateClient(world.Api.Server.BaseAddress);
            try
            {
                await replicas.OpenAsync(world.Database, world.Api.Server.PullAddress(world.Database), pulls, token);
                await overlay.StartAsync(token);
                await overlay.Ready.WaitAsync(TimeSpan.FromSeconds(5), token);
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
