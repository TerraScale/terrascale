using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

/// <summary>Real listener refusal and recovery against an independent topology file.</summary>
public sealed class PeerTopologyTests
{
    [Test]
    [Arguments("Development")]
    [Arguments("Production")]
    public async Task Standalone_starts_with_an_admitted_one_node_topology(string environment)
    {
        var bucket = new FakeS3();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await TerraBaseTestServer.StartAsync(bucket.Configure, services =>
        {
            bucket.Route(services);
            services.AddSingleton<TimeProvider>(clock);
        }, environmentName: environment);
        using var client = new HttpClient { BaseAddress = server.ClientAddress };
        await AssertCodeAsync(client, "/readyz", HttpStatusCode.OK);
        var verdict = await server.Client.GetFromJsonAsync<JsonElement>(new Uri("/v1/health/topology", UriKind.Relative));
        var options = server.Services.GetRequiredService<TerraBaseOptions>();
        await Assert.That(verdict.GetProperty("mismatch").GetInt32()).IsEqualTo(0);
        await Assert.That(verdict.GetProperty("generation").GetInt64()).IsEqualTo(1);
        var peer = verdict.GetProperty("peers").EnumerateArray().Single();
        await Assert.That(peer.GetProperty("node").GetString()).IsEqualTo(options.MachineId);
        await Assert.That(peer.GetProperty("region").GetString()).IsEqualTo(options.Region);
        await Assert.That(peer.GetProperty("address").GetString()).IsEqualTo("127.0.0.1");
        // An immutable standalone admission needs no ts-orch heartbeat.
        clock.Advance(TimeSpan.FromMinutes(1));
        await AssertCodeAsync(client, "/readyz", HttpStatusCode.OK);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cluster_identity_requires_admission_even_for_a_single_node(bool multiNode)
    {
        var root = Directory.CreateTempSubdirectory("unadmitted-topology-").FullName;
        try
        {
            var file = Path.Combine(root, "peers");
            await File.WriteAllTextAsync(file, "local test-region 127.0.0.1\n" + (multiNode ? "remote sjc 127.0.0.2\n" : string.Empty));
            var bucket = new FakeS3();
            await using var server = await TerraBaseTestServer.StartAsync(bucket.Configure, services =>
            {
                bucket.Route(services);
                var options = (TerraBaseOptions)services.Last(service => service.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
                services.AddSingleton(options with { PeersFile = file });
                services.AddSingleton<IMemberDirectory>(provider => new PeersFile(provider.GetRequiredService<TerraBaseOptions>()));
            }, environmentName: "Production");
            using var client = new HttpClient { BaseAddress = server.ClientAddress };
            await AssertCodeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable);
            await AssertCodeAsync(client, "/v1/projects/project/databases", HttpStatusCode.ServiceUnavailable);
            var verdict = await server.Client.GetFromJsonAsync<JsonElement>(new Uri("/v1/health/topology", UriKind.Relative));
            await Assert.That(verdict.GetProperty("mismatch").GetInt32()).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments("missing")]
    [Arguments("truncated")]
    [Arguments("malformed")]
    [Arguments("duplicate")]
    [Arguments("stale")]
    [Arguments("future")]
    [Arguments("runtime")]
    [Arguments("runtime-links")]
    [Arguments("duplicate-property")]
    [Arguments("regions")]
    [Arguments("oversized")]
    [Arguments("drain")]
    [Arguments("timeout")]
    [Arguments("self-address")]
    [Arguments("alias")]
    public async Task Drift_closes_serving_but_preserves_diagnostics_and_recovers(string damage)
    {
        var root = Directory.CreateTempSubdirectory("peer-topology-").FullName;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var file = Path.Combine(root, "peers");
        var manifest = Path.Combine(root, "topology.json");
        var generation = 7L;
        var ownAddress = "127.0.0.1";
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hang = false;
        var links = new ConfiguredLinks();
        var bucket = new FakeS3();
        const string complete = "local test-region 127.0.0.1\n";
        try
        {
            await File.WriteAllTextAsync(file, complete);
            var runtimeFile = Path.Combine(root, "runtime-peers");
            await File.WriteAllTextAsync(runtimeFile, "local test-region 127.0.0.2\n");
            await WriteManifestAsync();
            await using var server = await TerraBaseTestServer.StartAsync(bucket.Configure, services =>
            {
                bucket.Route(services);
                ManualBackupHosting.Configure(services);
                var options = (TerraBaseOptions)services.Last(service => service.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
                services.AddSingleton(options with { PeersFile = file });
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IMemberDirectory>(provider => new PausableDirectory(new PeersFile(provider.GetRequiredService<TerraBaseOptions>()),
                    () => Volatile.Read(ref hang) ? stalled.Task : Task.CompletedTask));
                if (damage == "runtime")
                {
                    services.AddSingleton<IMembership>(provider => new DirectoryMembership(new PeersFile(options with { PeersFile = runtimeFile }), options,
                        clock, provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DirectoryMembership>>()));
                }
                if (damage == "runtime-links")
                {
                    services.AddSingleton<IRuntimePeerTopology>(links);
                }
            });
            using var client = new HttpClient { BaseAddress = server.ClientAddress };
            if (damage != "runtime")
            {
                await AssertVerdictAsync(0);
                await AssertCodeAsync(client, "/readyz", HttpStatusCode.OK);
            }

            switch (damage)
            {
                case "missing": File.Delete(manifest); break;
                case "truncated": await File.WriteAllTextAsync(file, string.Empty); break;
                case "malformed": await File.AppendAllTextAsync(file, "not a peer line\n"); break;
                case "duplicate": await File.WriteAllTextAsync(file, complete + complete); break;
                case "stale": await WriteManifestAsync(clock.GetUtcNow().AddSeconds(-31)); break;
                case "future": await WriteManifestAsync(clock.GetUtcNow().AddSeconds(6)); break;
                case "duplicate-property": await File.WriteAllTextAsync(manifest, "{\"generation\":7,\"generation\":8}"); break;
                case "regions": await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest)).Replace("\"regions\":[\"test-region\"]", "\"regions\":[\"test-region\",\"sjc\"]", StringComparison.Ordinal)); break;
                case "oversized": await File.WriteAllTextAsync(manifest, new string(' ', 65537)); break;
                case "timeout": Volatile.Write(ref hang, true); break;
                case "alias": await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest)).Replace("\"peers\":", "\"Peers\":[],\"peers\":", StringComparison.Ordinal)); break;
                case "self-address":
                    ownAddress = "127.0.0.2";
                    await File.WriteAllTextAsync(file, complete.Replace("127.0.0.1", ownAddress, StringComparison.Ordinal));
                    await WriteManifestAsync();
                    clock.Advance(TimeSpan.FromSeconds(6));
                    break;
                case "runtime-links":
                    await File.WriteAllTextAsync(file, complete + "remote sjc 127.0.0.2\n");
                    await WriteManifestAsync(remote: true);
                    clock.Advance(TimeSpan.FromSeconds(6));
                    var view = await server.Services.GetRequiredService<IMembership>().RefreshAsync(CancellationToken.None);
                    await Assert.That(view.Elsewhere.Single().Name).IsEqualTo("remote");
                    await Assert.That(new Uri(view.Elsewhere.Single().PeerAddress).Host).IsEqualTo("127.0.0.2");
                    break;
                case "drain":
                    await File.WriteAllTextAsync(file, complete + "remote sjc 127.0.0.2\n");
                    await WriteManifestAsync(remote: true);
                    clock.Advance(TimeSpan.FromSeconds(6));
                    await AssertVerdictAsync(0, 2);
                    // Unreachable peers remain admitted; omitting one without a manifest change closes serving.
                    await File.WriteAllTextAsync(file, complete);
                    break;
            }

            await AssertVerdictAsync(1, expectedAddress: damage is "self-address" or "runtime" ? "127.0.0.1" : null);
            await AssertCodeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable);
            await AssertCodeAsync(client, "/v1/projects/project/databases", HttpStatusCode.ServiceUnavailable);
            await AssertCodeAsync(server.Client, "/healthz", HttpStatusCode.OK);
            await AssertCodeAsync(server.Client, "/replication", HttpStatusCode.OK);
            await AssertCodeAsync(client, "/v1/health/topology", HttpStatusCode.NotFound);
            var metrics = await server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
            await Assert.That(metrics).Contains("terrabase_peer_topology_mismatch");
            await Assert.That(metrics).Contains("terrabase_peer_topology_generation_age_seconds");
            await Assert.That(metrics.Split('\n').Single(line => line.StartsWith("terrabase_peer_topology_mismatch{", StringComparison.Ordinal)).Split(' ')[1]).IsEqualTo("1");

            if (damage == "runtime-links")
            {
                var verdict = await server.Client.GetFromJsonAsync<JsonElement>(new Uri("/v1/health/topology", UriKind.Relative));
                await Assert.That(verdict.GetProperty("peers").GetArrayLength()).IsEqualTo(1);
                // Only configured links change: file, directory and membership already agree with the manifest.
                links.Replace([new("remote", "http://127.0.0.2:8081/", "sjc", string.Empty)]);
                await AssertVerdictAsync(0, 2);
                await AssertCodeAsync(client, "/readyz", HttpStatusCode.OK);
                links.Replace([]);
            }

            if (damage != "runtime")
            {
                Volatile.Write(ref hang, false);
                stalled.TrySetResult();
                ownAddress = "127.0.0.1";
                await File.WriteAllTextAsync(file, complete);
                if (damage is "drain" or "self-address" or "runtime-links")
                {
                    generation++;
                    clock.Advance(TimeSpan.FromSeconds(6));
                }

                await WriteManifestAsync();
                await AssertVerdictAsync(0);
                await AssertCodeAsync(client, "/readyz", HttpStatusCode.OK);
                clock.Advance(TimeSpan.FromSeconds(31));
                // Expiry closes the request gate even before the background comparison runs again.
                await AssertCodeAsync(client, "/v1/projects/project/databases", HttpStatusCode.ServiceUnavailable);
            }

            async Task AssertVerdictAsync(int expected, int peerCount = 1, string? expectedAddress = null)
            {
                await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                {
                    var verdict = await server.Client.GetFromJsonAsync<JsonElement>(new Uri("/v1/health/topology", UriKind.Relative), cancellationToken);
                    return verdict.GetProperty("mismatch").GetInt32() == expected && (expected != 0 || verdict.GetProperty("peers").GetArrayLength() == peerCount)
                        && (expectedAddress is null || verdict.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("address").GetString() == expectedAddress));
                });
                using var response = await server.Client.GetAsync(new Uri("/v1/health/topology", UriKind.Relative));
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(response.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
                var body = JsonElement.Parse(await response.Content.ReadAsStringAsync());
                await Assert.That(body.GetProperty("mismatch").GetInt32()).IsEqualTo(expected);
                if (expected == 0)
                {
                    await Assert.That(body.GetProperty("generation").GetInt64()).IsEqualTo(generation);
                    await Assert.That(body.GetProperty("peers").GetArrayLength()).IsEqualTo(peerCount);
                }
            }

            Task WriteManifestAsync(DateTimeOffset? at = null, bool remote = false)
            {
                string[] regions = remote ? ["test-region", "sjc"] : ["test-region"];
                var peers = new List<object> { new { node = "local", region = "test-region", address = ownAddress } };
                if (remote)
                {
                    peers.Add(new { node = "remote", region = "sjc", address = "127.0.0.2" });
                }

                return File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { generation, observedAt = at ?? clock.GetUtcNow(), regions, peers }));
            }
        }
        finally
        {
            stalled.TrySetResult();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertCodeAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        await Assert.That(response.StatusCode).IsEqualTo(expected);
    }

    private sealed class PausableDirectory(IMemberDirectory directory, Func<Task> beforeRead) : IMemberDirectory
    {
        public async Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken)
        {
            await beforeRead(); // Deliberately ignores cancellation to prove the outer comparison budget.
            return await directory.ListAsync(cancellationToken);
        }
    }

    private sealed class ConfiguredLinks : IRuntimePeerTopology
    {
        private ClusterMember[] _peers = [];

        public IReadOnlyList<ClusterMember> ConfiguredPeers() => [.. Volatile.Read(ref _peers)];

        public void Replace(ClusterMember[] peers) => Volatile.Write(ref _peers, peers);
    }
}
