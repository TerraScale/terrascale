using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Cluster;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.CrossRegion;

/// <summary>
/// The partition suite across two regions (TS-643, docs/cross-region.md). gru-1 and sfo-1 have two members each, a catalog
/// primary and its follower that replicate to each other over the peer link. The link between the regions drops,
/// delays, duplicates and corrupts replies, and is partitioned and healed, while members restart and writers on every
/// member put and delete documents. Afterwards every member serves the same state, every acknowledged write that nobody
/// else wrote is there, and every document holds a value some writer sent: nothing was lost and nothing corrupt applied.
/// </summary>
public sealed class CrossRegionPartitionTests
{
    private const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";
    private static readonly string[] Scopes = ["documents:read", "documents:write"];
    private static readonly string[] InSfo = ["sfo-1"];

    [Test]
    public async Task Regions_converge_without_losing_or_corrupting_writes_through_a_lossy_partitioned_link_and_restarts()
    {
        var seed = Random.Shared.Next();
        // A failure names the seed: the faults and the writes repeat with it.
        Console.WriteLine($"cross-region partition suite seed {seed}");
        var faker = new Faker { Random = new Randomizer(seed) };
        using var peerTls = new PeerTls();
        var network = new FakeRegionNetwork { Faults = new RegionFaults(Drop: 0.1, Duplicate: 0.1, Corrupt: 0.05, MaxDelay: TimeSpan.FromMilliseconds(40), seed) };
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-partition-").FullName;
        var credentials = Path.Combine(directory, "user.creds");
        await File.WriteAllTextAsync(credentials, Credentials);
        var members = new Member[]
        {
            new("gru-1", "gru", "gru-a", TerraBaseTestServer.FreePort(), Path.Combine(directory, "gru-a")),
            new("gru-1", "gru", "gru-b", TerraBaseTestServer.FreePort(), Path.Combine(directory, "gru-b")),
            new("sfo-1", "sfo", "sfo-a", TerraBaseTestServer.FreePort(), Path.Combine(directory, "sfo-a")),
            new("sfo-1", "sfo", "sfo-b", TerraBaseTestServer.FreePort(), Path.Combine(directory, "sfo-b")),
        };
        var stores = new Dictionary<string, FakeMemberStore>
        {
            ["gru"] = new(TimeProvider.System),
            ["sfo"] = new(TimeProvider.System),
        };
        try
        {
            foreach (var member in members)
            {
                _ = Directory.CreateDirectory(member.Data);
                await member.StartAsync(peerTls, network, stores[member.Cell], credentials, members);
            }

            // The project in both regions; the canary at home in gru with a replica in sfo. A key per region (issued on its
            // primary, served by its follower).
            var (organization, project, database) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
            foreach (var primary in members.Where(member => member.Name.EndsWith("-a", StringComparison.Ordinal)))
            {
                using var service = primary.Api!.Service(primary.Api.User);
                using var registered = await service.PostAsync(
                    new Uri("/v1/management/commits", UriKind.Relative),
                    new StringContent(Registration(organization, project, primary.Cell == "gru" ? database : null), Encoding.UTF8, "application/json"));
                registered.EnsureSuccessStatusCode();
                var key = await primary.Api.Server.IssueAsync(organization, Scopes);
                foreach (var member in members.Where(member => member.Cell == primary.Cell))
                {
                    member.Key = key;
                }
            }

            var path = $"/v1/projects/{project}/databases/canary";
            using (var creator = members[0].Client())
            {
                (await creator.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
            }

            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                foreach (var member in members)
                {
                    using var client = member.Client();
                    if ((await client.GetAsync(new Uri($"{path}/collections/items", UriKind.Relative), token)).StatusCode != HttpStatusCode.OK)
                    {
                        return false;
                    }
                }

                return true;
            });

            // Writers on every member: shared documents that every member puts and deletes, and one solo document per
            // acknowledged write that nobody else writes. Each attempted value is recorded before it is sent.
            var shared = Enumerable.Range(0, 16).Select(i => ReplicationDocuments.Id($"shared-{i}")).ToArray();
            var attempted = new ConcurrentDictionary<string, ConcurrentBag<string>>(StringComparer.Ordinal);
            var solo = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var plans = members.Select(member => Enumerable.Range(0, 400).Select(_ => (Shared: faker.PickRandom(shared), Delete: faker.Random.Bool(0.2f), Value: faker.Random.Utf16String(1, 48))).ToArray()).ToArray();
            using var writing = new CancellationTokenSource();
            var writers = members.Select((member, index) => Task.Run(async () =>
            {
                var step = 0;
                while (!writing.IsCancellationRequested)
                {
                    var (sharedId, delete, value) = plans[index][step++ % plans[index].Length];
                    var tagged = $"{member.Name}-{step}-{value}";
                    try
                    {
                        using var client = member.Client();
                        if (step % 4 == 0)
                        {
                            var id = ReplicationDocuments.Id($"solo-{member.Name}-{step}");
                            using var response = await client.PutAsJsonAsync(new Uri($"{path}/collections/items/documents/by-key?id={id}", UriKind.Relative), new { value = tagged });
                            if (response.IsSuccessStatusCode)
                            {
                                solo[id] = tagged;
                            }
                        }
                        else if (delete)
                        {
                            using var response = await client.DeleteAsync(new Uri($"{path}/collections/items/documents/by-key?id={sharedId}", UriKind.Relative));
                        }
                        else
                        {
                            attempted.GetOrAdd(sharedId, _ => []).Add(tagged);
                            using var response = await client.PutAsJsonAsync(new Uri($"{path}/collections/items/documents/by-key?id={sharedId}", UriKind.Relative), new { value = tagged });
                        }
                    }
                    catch (Exception error) when (error is HttpRequestException or ObjectDisposedException or InvalidOperationException or TaskCanceledException)
                    {
                        // The member restarts; the write is unacknowledged and may or may not have committed.
                        await Task.Delay(TimeSpan.FromMilliseconds(100));
                    }
                }
            })).ToArray();

            // The schedule: a partition, member restarts on both sides during writes and during the partition, then a heal.
            await Task.Delay(TimeSpan.FromSeconds(3));
            network.Partition("gru", "sfo");
            await Task.Delay(TimeSpan.FromSeconds(2));
            await members[3].RestartAsync(peerTls, network, stores["sfo"], credentials, members);
            await Task.Delay(TimeSpan.FromSeconds(3));
            network.Heal();
            await Task.Delay(TimeSpan.FromSeconds(2));
            await members[1].RestartAsync(peerTls, network, stores["gru"], credentials, members);
            await Task.Delay(TimeSpan.FromSeconds(4));
            await writing.CancelAsync();
            await Task.WhenAll(writers);

            // Every member converges on one state; the lossy link stays on.
            string? converged = null;
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                var states = new List<string>();
                foreach (var member in members)
                {
                    // No sample yet: the member has not finished a round since it started.
                    if (await CountAsync(member, "terrabase_wan_lag", "", token, missing: -1) != 0)
                    {
                        return false;
                    }

                    states.Add(await StateAsync(member, path, shared.Concat(solo.Keys), token));
                }

                converged = states[0];
                return states.All(state => state == states[0]);
            });

            using var reader = members[2].Client();
            foreach (var (id, value) in solo)
            {
                // An acknowledged write that nobody else wrote is everywhere.
                var body = await reader.GetFromJsonAsync<JsonElement>(new Uri($"{path}/collections/items/documents/by-key?id={id}", UriKind.Relative));
                await Assert.That(body.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
            }

            foreach (var id in shared)
            {
                // A shared document is deleted or holds a value one of the writers sent: nothing else, nothing corrupt.
                using var response = await reader.GetAsync(new Uri($"{path}/collections/items/documents/by-key?id={id}", UriKind.Relative));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var value = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("value").GetString();
                    await Assert.That(attempted.TryGetValue(id, out var values) && values.Contains(value!)).IsTrue();
                }
                else
                {
                    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }
            }

            await Assert.That(solo.Count).IsGreaterThan(20);
            await Assert.That(converged).IsNotNull();
            // The link lost and corrupted replies (the checks above show nothing corrupt was applied), and groups crossed both ways.
            await Assert.That(network.Dropped).IsGreaterThan(0);
            await Assert.That(network.Corrupted).IsGreaterThan(0);
            foreach (var member in members)
            {
                await Assert.That(await member.Api!.Server.MetricAsync("terrabase_wan_pulled_groups_total")).IsGreaterThan(0);
            }
        }
        finally
        {
            // Followers first: a catalog primary's shutdown waits for its followers' open catalog streams.
            foreach (var member in members.Reverse())
            {
                if (member.Api is not null)
                {
                    await member.Api.DisposeAsync();
                }
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Registration(string organization, string project, string? database) => JsonSerializer.Serialize(new
    {
        id = EntityIds.New().ToString(),
        ops = new object?[]
        {
            new { op = "create_organization", id = organization, name = $"partition-{organization[..8]}" },
            new { op = "create_project", organization, id = project, name = "partition" },
            database is null ? null : new { op = "create_database", organization, project, name = "canary", id = database, replica_regions = InSfo },
        }.OfType<object>(),
    });

    // Every document's status, version and body on one member, in id order.
    private static async Task<string> StateAsync(Member member, string path, IEnumerable<string> ids, CancellationToken token)
    {
        using var client = member.Client();
        var state = new StringBuilder();
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            using var response = await client.GetAsync(new Uri($"{path}/collections/items/documents/by-key?id={id}", UriKind.Relative), token);
            _ = state.Append(id).Append(' ').Append((int)response.StatusCode).Append(' ').Append(response.Headers.ETag?.Tag).Append(' ')
                .Append(response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync(token) : "").Append('\n');
        }

        return state.ToString();
    }

    // The member's samples of a metric, or missing when it has none (yet).
    private static async Task<double> CountAsync(Member member, string name, string label, CancellationToken token, double missing = 0)
    {
        try
        {
            return await member.Api!.Server.MetricAsync(name, label, token);
        }
        catch (InvalidOperationException)
        {
            return missing;
        }
    }

    // A member of one of the two regions: its own data directory and peer port, kept across restarts.
    private sealed class Member(string region, string cell, string name, int port, string data)
    {
        public string Region { get; } = region;

        public string Cell { get; } = cell;

        public string Name { get; } = name;

        public string Data { get; } = data;

        public ManagementApi? Api { get; private set; }

        public string? Key { get; set; }

        // A client of this member's current process; the caller disposes it.
        public HttpClient Client() => (Api ?? throw new InvalidOperationException("the member is restarting")).DataClient(Key);

        public async Task StartAsync(PeerTls peerTls, FakeRegionNetwork network, FakeMemberStore store, string credentials, IReadOnlyList<Member> all)
        {
            var peers = all.Where(member => member.Cell == Cell).ToList();
            Api = await ManagementApi.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                settings["data-directory"] = Data;
                settings["peer-port"] = port.ToString(CultureInfo.InvariantCulture);
                settings["peers"] = string.Join(',', peers.Select(member => $"{member.Name}=localhost:{member.PeerPort}"));
                settings["catalog-primary"] = peers[0].Name;
                settings["region"] = Region;
                settings["node-name"] = Name;
                settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
                settings["nats-url"] = "nats://127.0.0.1:4222";
                settings["nats-credentials-file"] = credentials;
            }, services =>
            {
                services.AddSingleton(network.Join(Cell, Name));
                services.AddSingleton<IMemberStore>(store);
            }, managementCredential: true);
        }

        public int PeerPort => port;

        public async Task RestartAsync(PeerTls peerTls, FakeRegionNetwork network, FakeMemberStore store, string credentials, IReadOnlyList<Member> all)
        {
            var stopped = Api!;
            Api = null;
            await stopped.DisposeAsync();
            await StartAsync(peerTls, network, store, credentials, all);
        }
    }
}
