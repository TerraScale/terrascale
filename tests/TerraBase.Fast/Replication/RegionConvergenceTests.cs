using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// gru, sjc and fra serving one database: concurrent writes of one document converge to the newest version by HLC, with the
/// node as tiebreak, and every region serves another region's writes to its change feed readers and to SQL over the
/// PostgreSQL wire protocol.
/// </summary>
public sealed class RegionConvergenceTests
{
    [Test]
    public async Task Concurrent_puts_of_one_document_in_three_regions_converge_to_one_of_them_everywhere()
    {
        await using var cell = await Cell.StartAsync();
        await cell.SeedCollectionAsync();
        for (var round = 0; round < 5; round++)
        {
            var id = EntityIds.New().ToString();
            var written = await Task.WhenAll(cell.Machines.Select(machine => PutAsync(machine.Data, cell, id, $$"""{"region":"{{machine.Region}}","round":{{round}}}""")));

            // Every region ends with the same body and the same ETag, and that body is one of the three writes.
            (string Body, string? ETag)[] read = [];
            await TerraBaseTestServer.EventuallyAsync(async _ =>
            {
                read = await Task.WhenAll(cell.Machines.Select(machine => ReadAsync(machine.Data, cell, id)));
                return read.Distinct().Count() == 1;
            }, timeout: TimeSpan.FromSeconds(30));
            await Assert.That(written).Contains(read[0].Body).Because($"round {round}: {string.Join(" | ", read)}");
        }
    }

    [Test]
    public async Task A_later_write_in_another_region_wins_everywhere_and_a_concurrent_delete_and_put_converge()
    {
        await using var cell = await Cell.StartAsync();
        await cell.SeedCollectionAsync();
        var id = EntityIds.New().ToString();
        _ = await PutAsync(cell.Gru.Data, cell, id, """{"v":1}""");
        await ReplicatedAsync(cell, id, """{"v":1}""");

        // sjc saw gru's write, so its write is newer by HLC and replaces it in every region.
        _ = await PutAsync(cell.Sjc.Data, cell, id, """{"v":2}""");
        await ReplicatedAsync(cell, id, """{"v":2}""");

        // fra deletes while gru rewrites: each region ends with the same answer, absent or the same document.
        await Task.WhenAll(
            DeleteAsync(cell.Fra.Data, cell, id),
            PutAsync(cell.Gru.Data, cell, id, """{"v":3}"""));
        (string Body, string? ETag)[] read = [];
        await TerraBaseTestServer.EventuallyAsync(async _ =>
        {
            read = await Task.WhenAll(cell.Machines.Select(machine => ReadAsync(machine.Data, cell, id)));
            return read.Distinct().Count() == 1;
        }, timeout: TimeSpan.FromSeconds(30));
        await Assert.That(read[0].Body is """{"v":3}""" or "absent").IsTrue().Because(string.Join(" | ", read));
    }

    [Test]
    public async Task Concurrent_puts_resolve_by_hlc_and_an_equal_stamp_by_node_in_every_region()
    {
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru", "sjc", "fra");
        var (gru, sjc, fra) = (cluster["gru"], cluster["sjc"], cluster["fra"]);
        var stamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16;
        var (newest, tied) = (EntityIds.New().ToString(), EntityIds.New().ToString());

        // Each region writes before it received the others. The newest stamp wins wherever it arrives; on an equal stamp
        // the higher node does, so both orders of arrival end in the same document.
        await Task.WhenAll(
            gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp + 2, newest, """{"from":"gru"}"""), Put(gru, stamp + 5, tied, """{"from":"gru"}""")),
            sjc.CommitAsync(Put(sjc, stamp + 3, newest, """{"from":"sjc"}"""), Put(sjc, stamp + 5, tied, """{"from":"sjc"}""")),
            fra.CommitAsync(Put(fra, stamp + 1, newest, """{"from":"fra"}"""), Put(fra, stamp + 4, tied, """{"from":"fra"}""")));

        var higher = gru.Node.Value > sjc.Node.Value ? "gru" : "sjc";
        foreach (var machine in cluster.Machines)
        {
            await Assert.That(await machine.UntilAsync(newest, """{"from":"sjc"}""")).IsTrue().Because(machine.Region);
            await Assert.That(await machine.UntilAsync(tied, $$"""{"from":"{{higher}}"}""")).IsTrue().Because(machine.Region);
        }
    }

    [Test]
    public async Task Every_change_feed_reader_in_two_regions_receives_another_regions_writes_in_its_commit_order()
    {
        await using var cell = await Cell.StartAsync();
        await cell.SeedCollectionAsync();
        var changes = $"{cell.Managed.Data}/changes?cursor=now&collections={Cell.Collection}";
        var readers = new List<Sse>();
        try
        {
            foreach (var machine in new[] { cell.Sjc, cell.Fra })
            {
                for (var i = 0; i < 8; i++)
                {
                    var reader = await Sse.OpenAsync(machine.Data, changes);
                    await Assert.That((await reader.NextAsync()).Event).IsEqualTo("cursor");
                    readers.Add(reader);
                }
            }

            var ids = Enumerable.Range(0, 20).Select(_ => EntityIds.New().ToString()).ToArray();
            foreach (var (id, n) in ids.Select((id, n) => (id, n)))
            {
                _ = await PutAsync(cell.Gru.Data, cell, id, $$"""{"n":{{n}}}""");
            }

            var received = await Task.WhenAll(readers.Select(reader => reader.ChangesAsync(ids.Length)));
            foreach (var (events, index) in received.Select((events, index) => (events, index)))
            {
                await Assert.That(string.Join(',', events.Select(change => change.GetProperty("id").GetString()))).IsEqualTo(string.Join(',', ids))
                    .Because($"reader {index}");
                await Assert.That(events.Select(change => change.GetProperty("origin").GetString()).Distinct().Count()).IsEqualTo(1).Because($"reader {index}");
                await Assert.That(string.Join(',', events.Select(change => change.GetProperty("document").GetProperty("n").GetInt32()))).IsEqualTo(string.Join(',', Enumerable.Range(0, ids.Length)))
                    .Because($"reader {index}");
            }
        }
        finally
        {
            readers.ForEach(reader => reader.Dispose());
        }
    }

    private static async Task<string> PutAsync(HttpClient http, Cell cell, string id, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PutAsync(ManagementApi.Uri(ManagementApi.DocumentPath(cell.Managed, Cell.Collection, id)), content);
        await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        return json;
    }

    private static async Task DeleteAsync(HttpClient http, Cell cell, string id)
    {
        using var response = await http.DeleteAsync(ManagementApi.Uri(ManagementApi.DocumentPath(cell.Managed, Cell.Collection, id)));
        await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
    }

    // The document's JSON and ETag on the machine of `http`, or "absent".
    private static async Task<(string Body, string? ETag)> ReadAsync(HttpClient http, Cell cell, string id)
    {
        using var response = await http.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(cell.Managed, Cell.Collection, id)));
        return response.StatusCode == HttpStatusCode.NotFound
            ? ("absent", null)
            : (JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("document").GetRawText(), response.Headers.ETag?.Tag);
    }

    // Until every region reads `expected` for `id`.
    private static Task ReplicatedAsync(Cell cell, string id, string expected) =>
        TerraBaseTestServer.EventuallyAsync(async _ =>
            (await Task.WhenAll(cell.Machines.Select(machine => ReadAsync(machine.Data, cell, id)))).All(read => read.Body == expected),
            timeout: TimeSpan.FromSeconds(30));

    private static ChangeDelta Put(PeerStreamTests.Machine machine, ulong stamp, string id, string json)
    {
        var at = new EntryVersion(new Hlc(stamp), machine.Node);
        using var body = JsonDocument.Parse(json);
        return new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }

    // The collection's metadata document, as a local creation writes it in the same transaction.
    private static ChangeDelta Metadata(PeerStreamTests.Machine machine, ulong stamp, string collection)
    {
        var at = new EntryVersion(new Hlc(stamp), machine.Node);
        using var body = JsonDocument.Parse($$"""{"created_at":{{(long)(stamp >> 16)}},"entity_id":"{{EntityIds.New()}}"}""");
        return new ChangeDelta(DocumentDelta.Collections, collection, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }

    // gru, sjc and fra replicating one database through their CDC tailers, each serving the public APIs.
    private sealed class Cell : IAsyncDisposable
    {
        public const string Collection = "items";

        public FakeMemberStore Members { get; } = new();

        public string User { get; } = EntityIds.New().ToString();

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        public ManagedDatabaseRef Managed => new(Database.OrganizationId, Database.ProjectId!, Database.DatabaseId!, "main");

        public Machine Gru { get; private set; } = null!;

        public Machine Sjc { get; private set; } = null!;

        public Machine Fra { get; private set; } = null!;

        public Machine[] Machines => [Gru, Sjc, Fra];

        public static async Task<Cell> StartAsync()
        {
            var cell = new Cell();
            try
            {
                cell.Gru = new Machine(cell, "gru", ["sjc", "fra"]);
                cell.Sjc = new Machine(cell, "sjc", ["gru", "fra"]);
                cell.Fra = new Machine(cell, "fra", ["gru", "sjc"]);
                foreach (var machine in cell.Machines)
                {
                    await machine.StartAsync();
                }

                foreach (var machine in cell.Machines)
                {
                    await machine.ConnectedAsync();
                }

                return cell;
            }
            catch
            {
                await cell.DisposeAsync();
                throw;
            }
        }

        // gru creates the collection; a first document reaching every region shows they all hold it.
        public async Task SeedCollectionAsync()
        {
            await ManagementApi.CreateCollectionAsync(Gru.Data, Managed, Collection);
            var seed = EntityIds.New().ToString();
            _ = await PutAsync(Gru.Data, this, seed, """{"seed":true}""");
            await ReplicatedAsync(this, seed, """{"seed":true}""");
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

    private sealed class Machine(Cell cell, string region, string[] replicas) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-region-convergence-").FullName;
        private readonly FakeS3 _s3 = new();
        private ManagementApi? _api;
        private HttpClient? _data;

        public string Region => region;

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");

        public HttpClient Data => _data ?? throw new InvalidOperationException($"{region} is stopped");

        public async Task StartAsync()
        {
            _api = await ManagementApi.StartAsync(settings =>
            {
                _s3.Configure(settings);
                settings["region"] = region;
                settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
                settings["data-directory"] = _directory;
            }, services =>
            {
                _s3.Route(services);
                ManualBackupHosting.Configure(services);
                services.AddSingleton<IMemberDirectory>(cell.Members);
            }, userId: cell.User);
            await RegisterAsync(_api);
            var gate = _api.Server.Services.GetRequiredService<ICatalogGate>();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gate.IsOpen), timeout: TimeSpan.FromSeconds(30));
            _data = await _api.CreateClientAsync(cell.Database.OrganizationId, "documents:read", "documents:write");
        }

        // A write is admitted only while a stream to another placed region is connected: wait for every one.
        public Task ConnectedAsync() =>
            TerraBaseTestServer.EventuallyAsync(_ =>
            {
                var peers = Api.Server.Services.GetRequiredService<IReplicationStatus>().Snapshot().Peers.Select(peer => peer.Region).ToHashSet(StringComparer.Ordinal);
                return Task.FromResult(replicas.All(peers.Contains));
            }, timeout: TimeSpan.FromSeconds(30));

        public async ValueTask DisposeAsync()
        {
            try
            {
                _data?.Dispose();
                if (_api is not null)
                {
                    await _api.DisposeAsync();
                }
            }
            finally
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private async Task RegisterAsync(ManagementApi api)
        {
            var database = cell.Database;
            var commit = JsonSerializer.Serialize(new
            {
                id = EntityIds.New().ToString(),
                ops = new object[]
                {
                    new { op = "create_organization", id = database.OrganizationId, name = $"convergence-{database.OrganizationId}" },
                    new { op = "create_project", organization = database.OrganizationId, id = database.ProjectId, name = "convergence" },
                    new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "main", id = database.DatabaseId, replica_regions = replicas },
                },
            });
            using var service = api.Service(api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>An open SSE response read one event at a time.</summary>
    private sealed class Sse(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public static async Task<Sse> OpenAsync(HttpClient client, string uri)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.Relative));
            request.Headers.Accept.ParseAdd("text/event-stream");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var text = await response.Content.ReadAsStringAsync();
                response.Dispose();
                throw new InvalidOperationException($"the stream answered {response.StatusCode}: {text}");
            }

            return new Sse(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
        }

        public async Task<(string Event, JsonElement Data)> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? name = null, data = null;
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    using var json = JsonDocument.Parse(data ?? throw new InvalidDataException("an event without data"));
                    return (name ?? "message", json.RootElement.Clone());
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                (name, data) = line[..colon] switch
                {
                    "event" => (line[(colon + 1)..].TrimStart(' '), data),
                    "data" => (name, data + line[(colon + 1)..].TrimStart(' ')),
                    _ => (name, data),
                };
            }

            throw new EndOfStreamException("the stream ended");
        }

        // The data of the next `count` change events, skipping cursor heartbeats.
        public async Task<List<JsonElement>> ChangesAsync(int count)
        {
            var changes = new List<JsonElement>();
            while (changes.Count < count)
            {
                if (await NextAsync() is ("change", var data))
                {
                    changes.Add(data);
                }
            }

            return changes;
        }

        public void Dispose()
        {
            reader.Dispose();
            response.Dispose();
        }
    }
}
