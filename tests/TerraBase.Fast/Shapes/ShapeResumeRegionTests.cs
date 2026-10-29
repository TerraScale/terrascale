using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed partial class ShapeResumeTests
{
    [Test]
    public async Task A_client_synced_on_gru_resumes_on_sjc_and_replays_a_covered_row_committed_after_an_uncovered_one()
    {
        await using var cell = await Cell.StartAsync();
        var (gru, sjc) = (cell.Gru, cell.Sjc);
        await ManagementApi.CreateCollectionAsync(gru.Data, cell.Managed, Cell.Collection);
        var path = $"{cell.Managed.Data}/collections/{Cell.Collection}/shape";
        var (first, second, covered, uncovered) = (DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId());
        // A baseline from both origins; sjc learns the collection with gru's first document.
        await PutAsync(gru.Data, cell, first, """{"at":"gru"}""");
        await ReplicatedAsync(sjc.Data, cell, first, """{"at":"gru"}""");
        await PutAsync(sjc.Data, cell, second, """{"at":"sjc"}""");
        await ReplicatedAsync(gru.Data, cell, second, """{"at":"sjc"}""");
        await gru.DurableAsync();

        // sjc is down while gru writes C (fra confirms it) and the client syncs on gru, so the cursor covers C.
        await sjc.StopAsync();
        await PutAsync(gru.Data, cell, covered, """{"covered":"gru"}""");
        await gru.DurableAsync();
        Dictionary<string, string> state = [];
        var synced = await SyncAsync(gru.Data, path, null, state);
        await Assert.That(state.Keys).Contains(covered);

        // gru is down while sjc writes U, so sjc commits the uncovered U before the covered C reaches it.
        await gru.StopAsync();
        await sjc.StartAsync();
        await sjc.ConnectedAsync();
        await PutAsync(sjc.Data, cell, uncovered, """{"uncovered":"sjc"}""");
        await gru.StartAsync();
        await ReplicatedAsync(sjc.Data, cell, covered, """{"covered":"gru"}""");
        await ReplicatedAsync(gru.Data, cell, uncovered, """{"uncovered":"sjc"}""");

        await sjc.DurableAsync();
        var resumed = await ResumeAsync(sjc.Data, path, synced.Cursor);
        await Assert.That(resumed.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect).Because(resumed.Message ?? "no refusal message");
        await CacheControlAssert.EqualAsync(resumed.CacheControl);
        await Assert.That(resumed.Handle).IsNotEqualTo(synced.Handle);
        var location = resumed.Location!;
        var offset = location[(location.LastIndexOf("offset=", StringComparison.Ordinal) + "offset=".Length)..];
        await Assert.That(long.Parse(offset, CultureInfo.InvariantCulture)).IsGreaterThan(0).Because("the baseline precedes the target");

        // The target is the boundary before U: the baseline is not sent again, and C repeats after U (at least once).
        List<string> applied = [];
        var moved = await FollowAsync(sjc.Data, path, resumed.Location!, state, applied);
        await Assert.That(moved.Handle).IsEqualTo(resumed.Handle);
        await Assert.That(applied.SequenceEqual([uncovered, covered])).IsTrue().Because(string.Join(',', applied));
        await Assert.That(state.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { first, second, covered, uncovered }.Order(StringComparer.Ordinal))).IsTrue();
        await AssertSameAsync(state, await FreshAsync(sjc.Data, path));
    }

    private static async Task PutAsync(HttpClient http, Cell cell, string id, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PutAsync(ManagementApi.Uri(ManagementApi.DocumentPath(cell.Managed, Cell.Collection, id)), content);
        await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
    }

    // Until the machine of `http` reads `expected` for `id`.
    private static Task ReplicatedAsync(HttpClient http, Cell cell, string id, string expected) =>
        TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await http.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(cell.Managed, Cell.Collection, id)), token);
            return response.IsSuccessStatusCode
                && JsonElement.Parse(await response.Content.ReadAsStringAsync(token)).GetProperty("document").GetRawText() == expected;
        }, timeout: TimeSpan.FromSeconds(30));

    // gru, sjc and fra replicating one database. fra confirms writes while gru or sjc is stopped; streams carry only a
    // machine's own change groups, so it never relays gru's rows to sjc.
    private sealed class Cell : IAsyncDisposable
    {
        public const string Collection = "items";

        public FakeMemberStore Members { get; } = new();

        public FakeS3 Bucket { get; } = new();

        public string User { get; } = EntityIds.New().ToString();

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        public ManagedDatabaseRef Managed => new(Database.OrganizationId, Database.ProjectId!, Database.DatabaseId!, "main");

        public Machine Gru { get; private set; } = null!;

        public Machine Sjc { get; private set; } = null!;

        public Machine Fra { get; private set; } = null!;

        public static async Task<Cell> StartAsync()
        {
            var cell = new Cell();
            try
            {
                cell.Gru = new Machine(cell, "gru", ["sjc", "fra"]);
                cell.Sjc = new Machine(cell, "sjc", ["gru", "fra"]);
                cell.Fra = new Machine(cell, "fra", ["gru", "sjc"]);
                foreach (var machine in new[] { cell.Gru, cell.Sjc, cell.Fra })
                {
                    await machine.StartAsync();
                }

                foreach (var machine in new[] { cell.Gru, cell.Sjc, cell.Fra })
                {
                    await machine.ConnectedAsync(all: true);
                }

                return cell;
            }
            catch
            {
                await cell.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var machine in new[] { Fra, Sjc, Gru })
            {
                if (machine is not null)
                {
                    await machine.DisposeAsync();
                }
            }
        }
    }

    // One machine whose restarts reuse its data directory and node name, so it keeps its replica id and log.
    private sealed class Machine(Cell cell, string region, string[] replicas) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-shape-resume-").FullName;
        private readonly string _name = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        private ManagementApi? _api;
        private HttpClient? _data;

        public HttpClient Data => _data ?? throw new InvalidOperationException($"{region} is stopped");

        public async Task StartAsync()
        {
            var first = !File.Exists(Path.Combine(_directory, "_system.db"));
            _api = await ManagementApi.StartAsync(settings =>
            {
                cell.Bucket.Configure(settings);
                settings["region"] = region;
                settings["node-name"] = _name;
                settings["data-directory"] = _directory;
            }, services =>
            {
                cell.Bucket.Route(services);
                ManualBackupHosting.Configure(services);
                services.AddSingleton<IMemberDirectory>(cell.Members);
            }, userId: cell.User);
            if (first)
            {
                await RegisterAsync(_api);
            }

            var gate = _api.Server.Services.GetRequiredService<ICatalogGate>();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gate.IsOpen), timeout: TimeSpan.FromSeconds(30));
            _data = await _api.CreateClientAsync(cell.Database.OrganizationId, "documents:read", "documents:write");
        }

        // Anchors the catalog first: a restart while another region is down opens its catalog gate on a majority only with it.
        public async Task StopAsync()
        {
            var token = TestContext.Current!.Execution.CancellationToken;
            await Assert.That(await _api!.Server.Services.GetRequiredService<ICatalogGate>().TryAnchorAsync(token)).IsTrue();
            // fra stays online throughout both failovers. Before removing a witness's source, prove fra received its
            // final SYSTEM prefix; observing tenant durability or the local anchor write alone cannot prove that.
            var source = await _api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
            long cut;
            await using (var read = await source.BeginTransactionAsync(write: false, token))
            {
                cut = (await read.GetVersionAsync(CatalogGate.Revocations, _name, token))!.Value.Version;
            }
            var origin = _api.Server.Services.GetRequiredService<LocalReplica>().Id;
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
            {
                var destination = await cell.Fra._api!.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken);
                await using var read = await destination.BeginTransactionAsync(write: false, cancellationToken);
                var applied = await read.GetAppliedAsync(cancellationToken);
                return applied.TryGetValue(origin, out var held) && held.Value >= unchecked((ulong)cut);
            }, timeout: TimeSpan.FromSeconds(30));
            await CloseAsync();
        }

        // A write is admitted only while a stream to another placed region is connected; `all` waits for every one.
        public Task ConnectedAsync(bool all = false) =>
            TerraBaseTestServer.EventuallyAsync(_ =>
            {
                var peers = _api!.Server.Services.GetRequiredService<IReplicationStatus>().Snapshot().Peers.Select(peer => peer.Region).ToHashSet(StringComparer.Ordinal);
                return Task.FromResult(all ? replicas.All(peers.Contains) : replicas.Any(peers.Contains));
            }, timeout: TimeSpan.FromSeconds(30));

        // A bootstrap seals and a resume targets only at or below the remote durable frontier (FR-25), which passes a joined
        // commit only while a link to its origin's region is connected: until the retention pass has walked the whole head.
        public Task DurableAsync() =>
            TerraBaseTestServer.EventuallyAsync(async token =>
            {
                var store = (await _api!.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(cell.Database, token))!;
                await using var transaction = await store.BeginTransactionAsync(write: false, token);
                var shapes = (IShapeStorageTransaction)transaction;
                return await shapes.ReadShapeFrontierAsync(remote: true, token) == await shapes.ReadShapeFrontierAsync(remote: false, token);
            }, timeout: TimeSpan.FromSeconds(30));

        public async ValueTask DisposeAsync()
        {
            try
            {
                await CloseAsync();
            }
            finally
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private async Task CloseAsync()
        {
            _data?.Dispose();
            _data = null;
            if (_api is not null)
            {
                await _api.DisposeAsync();
                _api = null;
            }
        }

        private async Task RegisterAsync(ManagementApi api)
        {
            var database = cell.Database;
            await CatalogFixture.RegisterAsync(api, api.User, database, "main", replicas);
        }
    }
}
