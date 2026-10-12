using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// Peer streams between real machines over their internal listeners (docs/terrabase/multi-region-replication.md sections 8
/// and 9). Each machine's change groups come from a <see cref="FakeChangeGroupSource"/> until the CDC tailer (R3) lands: a
/// test commits deltas on a machine through the engine's join, as a local write would, and adds the same group to its source.
/// </summary>
public sealed class PeerStreamTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Test]
    public async Task Three_regions_converge_through_concurrent_reordered_and_duplicated_groups()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc", "fra");
        var (gru, sjc, fra) = (cluster["gru"], cluster["sjc"], cluster["fra"]);
        var stamp = Stamp();
        var id = EntityIds.New().ToString();

        // sjc's edit of a field commits before gru's create reaches anyone, fra edits another field and loses an older write
        // of the first, and gru ships its create twice: every machine ends with the same document.
        await sjc.CommitAsync(Set(sjc, stamp + 2, id, "a", "\"sjc\""));
        await gru.CommitAsync(
            Metadata(gru, stamp, "items"),
            Put(gru, stamp, id, """{"a":"gru","b":"gru","n":1}"""));
        await fra.CommitAsync(Set(fra, stamp + 1, id, "a", "\"fra\""), Set(fra, stamp + 3, id, "b", "\"fra\""));
        await gru.CommitAsync(Put(gru, stamp, id, """{"a":"gru","b":"gru","n":1}"""));

        foreach (var machine in cluster.Machines)
        {
            await Assert.That(await machine.UntilAsync(id, """{"a":"sjc","b":"fra","n":1}""")).IsTrue().Because(machine.Region);
        }

        // The collection came with its metadata document: the same identity everywhere.
        var collections = await Task.WhenAll(cluster.Machines.Select(machine => machine.CollectionIdAsync("items")));
        await Assert.That(collections.Distinct().Count()).IsEqualTo(1);

        // Every stream reports, nothing is behind, so each region's summary is ok.
        const string Ok = """{"region":"gru","status":"ok","maxLagSeconds":0,"peers":[{"region":"fra","status":"ok","lagSeconds":0},{"region":"sjc","status":"ok","lagSeconds":0}]}""";
        await Assert.That(await gru.HealthAsync(Ok)).IsEqualTo(Ok);
    }

    [Test]
    public async Task A_restarted_machine_catches_up_from_its_last_ack()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var stamp = Stamp();
        var id = EntityIds.New().ToString();
        await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, id, """{"n":1}"""));
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();
        // sjc acks only after its commit, so seeing the document there does not mean gru has recorded the ack yet.
        using (var deadline = new CancellationTokenSource(Deadline))
        {
            await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, 1, deadline.Token);
        }

        await Assert.That(gru.Source.AckedBy(cluster.Database, sjc.Node)).IsEqualTo(1);

        // Written while sjc is down: gru keeps the groups past sjc's ack and ships them once sjc is back.
        await sjc.StopAsync();
        await gru.CommitAsync(Set(gru, stamp + 1, id, "n", "2"));
        await gru.CommitAsync(Set(gru, stamp + 2, id, "m", "3"));
        await sjc.StartAsync();

        await Assert.That(await sjc.UntilAsync(id, """{"m":3,"n":2}""")).IsTrue();
        using (var deadline = new CancellationTokenSource(Deadline))
        {
            await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, 3, deadline.Token);
        }

        await Assert.That(gru.Source.AckedBy(cluster.Database, sjc.Node)).IsEqualTo(3);
    }

    [Test]
    public async Task A_malformed_batch_ends_the_stream_and_an_unplaced_database_is_refused()
    {
        await using var cluster = await Cluster.StartAsync("gru");
        var gru = cluster["gru"];
        using var channel = PeerChannel.Create(gru.Api.Server.BaseAddress);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var origin = ReplicaId.Of(1, 7, 0);
        var other = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        using var call = client.Push();
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome.Node).IsEqualTo(gru.Node.Value);

        // A database this machine does not hold: refused, nothing applied, and the stream goes on.
        var delta = Put(origin, Stamp(), EntityIds.New().ToString(), """{"n":1}""");
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = ChangeGroups.Wire(other), LastChangeId = 1, Groups = ChangeGroups.Encode([new OriginGroup(1, [delta])]) } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Ack.Refused).IsTrue();

        // Bytes that are no LZ4 envelope of groups: the receiver ends the stream.
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = ChangeGroups.Wire(cluster.Database), LastChangeId = 1, Groups = ByteString.CopyFromUtf8("forged") } });
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(CancellationToken.None));
        await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    // The first stamp of a test: now, so convergence lags stay meaningful; later operations add logical ticks.
    private static ulong Stamp() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16;

    private static ChangeDelta Put(Machine machine, ulong stamp, string id, string json) => Put(machine.Node, stamp, id, json);

    private static ChangeDelta Put(ReplicaId node, ulong stamp, string id, string json)
    {
        var at = new EntryVersion(new Hlc(stamp), node);
        using var body = JsonDocument.Parse(json);
        return new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }

    private static ChangeDelta Set(Machine machine, ulong stamp, string id, string field, string json)
    {
        var at = new EntryVersion(new Hlc(stamp), machine.Node);
        using var value = JsonDocument.Parse(json);
        return new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Set(at, field, value.RootElement).DeltaAt(at.Stamp));
    }

    // The collection's metadata document, as a local creation writes it in the same transaction.
    private static ChangeDelta Metadata(Machine machine, ulong stamp, string collection)
    {
        var at = new EntryVersion(new Hlc(stamp), machine.Node);
        using var body = JsonDocument.Parse($$"""{"created_at":{{(long)(stamp >> 16)}},"entity_id":"{{EntityIds.New()}}"}""");
        return new ChangeDelta(DocumentDelta.Collections, collection, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }

    /// <summary>One machine per region, all listed in one fake DNS, each holding the same database.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        private readonly Dictionary<string, Machine> _machines = [with(StringComparer.Ordinal)];

        public FakeMemberStore Members { get; } = new();

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        public IEnumerable<Machine> Machines => _machines.Values;

        public Machine this[string region] => _machines[region];

        public static async Task<Cluster> StartAsync(params string[] regions)
        {
            var cluster = new Cluster();
            try
            {
                foreach (var region in regions)
                {
                    var machine = new Machine(cluster, region, [.. regions.Where(other => other != region)]);
                    cluster._machines[region] = machine;
                    await machine.StartAsync();
                }

                return cluster;
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var machine in _machines.Values)
            {
                await machine.DisposeAsync();
            }
        }
    }

    private sealed class Machine(Cluster cluster, string region, string[] replicas) : IAsyncDisposable
    {
        private readonly string _name = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-peer-streams-").FullName;
        private ManagementApi? _api;

        public string Region { get; } = region;

        public FakeChangeGroupSource Source { get; } = new();

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{Region} is stopped");

        public ReplicaId Node => Api.Server.Services.GetRequiredService<LocalReplica>().Id;

        private IStorageEngine Engine => Api.Server.Services.GetRequiredService<IStorageEngine>();

        public async Task StartAsync()
        {
            var first = !File.Exists(Path.Combine(_directory, "_system.db"));
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = Region;
                settings["node-name"] = _name;
                settings["data-directory"] = _directory;
            }, services => services.AddSingleton<IMemberDirectory>(cluster.Members).AddSingleton<IChangeGroupSource>(Source));
            if (first)
            {
                await RegisterAsync();
            }
        }

        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
        }

        // Commits the deltas here, as a local write does, then hands them to the source as one group.
        public async Task CommitAsync(params ChangeDelta[] deltas)
        {
            var store = await Engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");
            await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                foreach (var delta in deltas)
                {
                    _ = await transaction.JoinAsync(delta.Collection, delta.Id, delta.State, new EntryVersion(delta.Stamp, Node), CancellationToken.None);
                }

                _ = await transaction.CommitAsync(CancellationToken.None);
            }

            _ = Source.Add(cluster.Database, deltas);
        }

        // Whether the document's materialized JSON becomes `expected` within the deadline.
        public async Task<bool> UntilAsync(string id, string expected)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                if (await ReadAsync(id) == expected)
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        public async Task<string?> CollectionIdAsync(string collection)
        {
            var store = await Engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None);
            await using var transaction = await store!.BeginTransactionAsync(write: false, CancellationToken.None);
            return (await transaction.GetCollectionAsync(collection, CancellationToken.None))?.Id;
        }

        // The summary once it reads `expected`, or the last one at the deadline: lag settles after the last acks.
        public async Task<string> HealthAsync(string expected)
        {
            var deadline = DateTime.UtcNow + Deadline;
            string body;
            do
            {
                using var response = await Api.Server.Client.GetAsync(new Uri("/health/replication", UriKind.Relative));
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                body = JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetRawText();
                await Task.Delay(100);
            }
            while (body != expected && DateTime.UtcNow < deadline);

            return body;
        }

        public async ValueTask DisposeAsync()
        {
            if (_api is not null)
            {
                await StopAsync();
            }

            Directory.Delete(_directory, recursive: true);
        }

        private async Task<string?> ReadAsync(string id)
        {
            if (await Engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None) is not { } store)
            {
                return null;
            }

            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            try
            {
                return await transaction.GetAsync("items", id, CancellationToken.None) is { Deleted: false } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // The organization, project and database under the cluster's ids, placed in every region of the cluster.
        private async Task RegisterAsync()
        {
            var database = cluster.Database;
            var commit = JsonSerializer.Serialize(new
            {
                id = EntityIds.New().ToString(),
                ops = new object[]
                {
                    new { op = "create_organization", id = database.OrganizationId, name = $"peers-{database.OrganizationId}" },
                    new { op = "create_project", organization = database.OrganizationId, id = database.ProjectId, name = "peers" },
                    new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "main", id = database.DatabaseId, replica_regions = replicas },
                },
            });
            using var service = Api.Service(Api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        }
    }
}
