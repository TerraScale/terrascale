using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Sqlite;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

public sealed class ReplicationChaosTests
{
    [Test]
    [Arguments(682)]
    [Arguments(683)]
    public async Task Three_nodes_preserve_acknowledged_writes_after_partition_skew_storage_stall_and_crash(int seed)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        await using var cluster = await ChaosCluster.StartAsync(token);
        var history = new ReplicationHistory(seed, cluster.Path);
        await WriteRoundAsync("baseline");
        await Assert.That(history.AcknowledgedCount).IsEqualTo(12);
        await ConvergedAsync(cluster, history, token);

        // Isolate the crashable member in both directions; the other two must still exchange writes.
        await cluster.IsolateAsync(2, token);
        var isolated = await history.PutAsync(cluster.Clients[2], 2, "partition", 0, token);
        await Assert.That(history.AcknowledgedCount).IsEqualTo(13);
        var connected = await history.PutAsync(cluster.Clients[0], 0, "partition", 0, token);
        await TerraBaseTestServer.EventuallyAsync(async cancellation =>
        {
            using var read = await cluster.Clients[1].GetAsync(cluster.Document(connected), cancellation);
            return read.IsSuccessStatusCode;
        }, TimeSpan.FromSeconds(30));
        using (var read = await cluster.Clients[1].GetAsync(cluster.Document(isolated), token))
        {
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // Kill while its acknowledged local write cannot have reached either peer. No graceful checkpoint.
        await cluster.Child!.CrashAsync();
        await history.PutAsync(cluster.Clients[1], 1, "offline", 0, token);
        await cluster.Child.RestartAsync(token);
        using (var recovered = await cluster.Clients[2].GetAsync(cluster.Document(isolated), token))
        {
            recovered.EnsureSuccessStatusCode();
        }

        cluster.Heal(2);
        await ConvergedAsync(cluster, history, token);

        // UTC changes independently of monotonic timers and logical document versions.
        cluster.Clocks[0].Shift(TimeSpan.FromHours(12));
        cluster.Clocks[1].Shift(TimeSpan.FromHours(-12));
        await WriteRoundAsync("skew");
        cluster.Clocks[0].Shift(TimeSpan.FromHours(-24));
        await WriteRoundAsync("backward-jump");
        await ConvergedAsync(cluster, history, token);

        // Stall the actual SQLite write executor, leaving reads and the two healthy writers running.
        var store = (SqliteDatabaseStore)(await cluster.Nodes[0]!.Services.GetRequiredService<SqliteStorageEngine>()
            .OpenDatabaseAsync(cluster.Tenant!.Database("chaos"), token))!;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = store.WriteLane.RunAsync(() =>
        {
            entered.TrySetResult();
            release.Wait(token);
        }, token);
        Task<string>? stalled = null;
        try
        {
            await entered.Task.WaitAsync(token);
            stalled = history.PutAsync(cluster.Clients[0], 0, "slow-storage", 0, token);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.WriteLane.Queued > 0), TimeSpan.FromSeconds(30));
            await Assert.That(stalled.IsCompleted).IsFalse();
            var forwarded = cluster.ObserveNextBatch(1, 0);
            var healthy = await history.PutAsync(cluster.Clients[1], 1, "slow-storage", 0, token);
            await history.PutAsync(cluster.Clients[2], 2, "slow-storage", 0, token);
            await TerraBaseTestServer.EventuallyAsync(async cancellation =>
            {
                using var read = await cluster.Clients[2].GetAsync(cluster.Document(healthy), cancellation);
                return read.IsSuccessStatusCode;
            }, TimeSpan.FromSeconds(30));
            await forwarded.WaitAsync(token);
            await Assert.That(stalled.IsCompleted).IsFalse();
            // Cut links with real streams waiting on the stalled receiver, not just future connections.
            await Assert.That((await cluster.IsolateAsync(0, token)).Sum()).IsGreaterThan(0);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(stalled is null ? [blocker] : [blocker, stalled]);
        }

        cluster.Heal(0);
        await WriteRoundAsync("recovered");
        await Assert.That(history.AcknowledgedCount).IsEqualTo(54);
        await ConvergedAsync(cluster, history, token);

        Task WriteRoundAsync(string phase)
        {
            return Task.WhenAll(Enumerable.Range(0, 3).Select(async node =>
            {
                for (var ordinal = 0; ordinal < 4; ordinal++)
                {
                    await history.PutAsync(cluster.Clients[node], node, phase, ordinal, token);
                }
            }));
        }
    }

    [Test]
    public async Task Failed_child_start_preserves_its_original_cause_and_output()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-chaos-start-").FullName;
        Task? cleanup = null;
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        try
        {
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["data-directory"] = directory,
                ["admin-port"] = TerraBaseTestServer.FreePort().ToString(CultureInfo.InvariantCulture),
                ["client-port"] = TerraBaseTestServer.FreePort().ToString(CultureInfo.InvariantCulture),
                ["peer-port"] = TerraBaseTestServer.FreePort().ToString(CultureInfo.InvariantCulture),
                ["region"] = "INVALID-REGION",
            };
            var error = await Assert.That(async () =>
            {
                await CrashableTerraBase.StartAsync(settings, token, owner => cleanup = owner);
            }).Throws<InvalidOperationException>();
            await Assert.That(error!.InnerException!.Message).Contains("exited before readiness with code 2");
            await Assert.That(error.Message).Contains("stderr:\nterrabase: region");
            await Assert.That(cleanup is not null).IsTrue();
            await cleanup!.WaitAsync(token);
        }
        finally
        {
            ReleaseAfterCleanup(cleanup ?? Task.CompletedTask, () => Directory.Delete(directory, recursive: true));
        }
    }

    private static async Task ConvergedAsync(ChaosCluster cluster, ReplicationHistory history, CancellationToken token)
    {
        await cluster.ReplicatedAsync(token);
        InvalidOperationException? lastFailure = null;
        try
        {
            // The process-hosted node uses the exporter's scrape cache. Its cached zero is only a hint;
            // HTTP snapshots must actually converge, with the last history retained if the deadline expires.
            await TerraBaseTestServer.EventuallyAsync(async cancellation =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation);
                try
                {
                    await history.CheckAsync(cluster.Clients, linked.Token);
                    return true;
                }
                catch (InvalidOperationException error)
                {
                    lastFailure = error;
                    return false;
                }
            }, TimeSpan.FromSeconds(60));
        }
        catch (OperationCanceledException) when (lastFailure is not null)
        {
            throw new InvalidOperationException($"chaos convergence deadline expired: {lastFailure.Message}", lastFailure);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task The_history_checker_rejects_a_lost_or_changed_acknowledged_write(bool changeBody)
    {
        var tls = new ClientTls();
        TerraBaseTestServer? node = null;
        Task? failedStart = null;
        try
        {
            node = await TerraBaseTestServer.StartAsync(tls.Apply, failedStartCleanup: cleanup => failedStart = cleanup);
            var tenant = await ReplicationTenant.CreateAsync(node, "chaos");
            using var client = tls.CreateClient(node.ClientAddress!, await tenant.KeyAsync(node, "documents:read", "documents:write"));
            var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
            using var collection = await client.PostAsJsonAsync(new Uri($"{tenant.Path("chaos")}/collections", UriKind.Relative), new { name = "items" }, token);
            collection.EnsureSuccessStatusCode();
            var history = new ReplicationHistory(682, tenant.Path("chaos"));
            // More than one page: a checker reading only its first page must fail even before the corruption.
            var ids = new List<string>();
            for (var ordinal = 0; ordinal < 15; ordinal++)
            {
                ids.Add(await history.PutAsync(client, 0, "checker", ordinal, token));
            }

            await Assert.That(history.AcknowledgedCount).IsEqualTo(15);
            await history.CheckAsync([client], token);
            var target = new Uri($"{tenant.Path("chaos")}/collections/items/documents/{ids.Order(StringComparer.Ordinal).Last()}", UriKind.Relative);
            using var corrupt = changeBody
                ? await client.PutAsJsonAsync(target, new { value = "corrupted" }, token)
                : await client.DeleteAsync(target, token);
            corrupt.EnsureSuccessStatusCode();
            var error = await Assert.That(() => history.CheckAsync([client], token)).Throws<InvalidOperationException>();
            await Assert.That(error!.Message).Contains("682");
        }
        finally
        {
            try
            {
                if (node is not null)
                {
                    await node.DisposeAsync();
                }
            }
            finally
            {
                ReleaseAfterCleanup(Task.WhenAll((node is null ? Array.Empty<Task>() : [node.CleanupCompletion])
                    .Concat(failedStart is null ? [] : [failedStart])), tls.Dispose);
            }
        }
    }

    // Cancellation bounds the caller's disposal wait; fixture files still belong to the native cleanup owner.
    private static void ReleaseAfterCleanup(Task cleanup, Action release)
    {
        if (cleanup.IsCompleted)
        {
            _ = cleanup.Exception;
            release();
            return;
        }

        _ = cleanup.ContinueWith(task =>
        {
            _ = task.Exception;
            try
            {
                release();
            }
            catch (Exception error)
            {
                TerraBaseTestServer.ReportCleanupFailure(new AggregateException(error));
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class ShiftedClock : TimeProvider
    {
        private long _offset;

        public void Shift(TimeSpan offset) => Interlocked.Exchange(ref _offset, offset.Ticks);

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _offset));
    }

    private sealed class ChaosCluster : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-chaos-").FullName;
        private readonly PeerTls _peers = new();
        private readonly ClientTls _tls = new();
        private readonly List<TerraBaseTestServer> _servers = [];
        private readonly List<Task> _failedStarts = [];
        private readonly PartitionablePeer?[,] _links = new PartitionablePeer?[3, 3];

        public TerraBaseTestServer?[] Nodes { get; } = new TerraBaseTestServer?[2];
        public ShiftedClock[] Clocks { get; } = [new(), new()];
        public CrashableTerraBase? Child { get; private set; }
        public ReplicationTenant? Tenant { get; private set; }
        public List<HttpClient> Clients { get; } = [];
        public string Path => Tenant!.Path("chaos");

        public Uri Document(string id) => new($"{Path}/collections/items/documents/{id}", UriKind.Relative);

        public static async Task<ChaosCluster> StartAsync(CancellationToken token)
        {
            var cluster = new ChaosCluster();
            try
            {
                var ports = Enumerable.Range(0, 3).Select(_ => TerraBaseTestServer.FreePort()).ToArray();
                for (var from = 0; from < 3; from++)
                {
                    for (var to = 0; to < 3; to++)
                    {
                        if (from != to)
                        {
                            cluster._links[from, to] = await PartitionablePeer.StartAsync(cluster._peers, new Uri($"https://localhost:{ports[to]}"));
                        }
                    }
                }

                var childSettings = cluster.Settings(2, ports[2]);
                childSettings["admin-port"] = TerraBaseTestServer.FreePort().ToString(CultureInfo.InvariantCulture);
                childSettings["client-port"] = TerraBaseTestServer.FreePort().ToString(CultureInfo.InvariantCulture);
                string key;
                // Provision the child's disk through the same real application harness before launching its process.
                await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
                {
                    cluster._tls.Apply(settings);
                    settings["data-directory"] = childSettings["data-directory"];
                }, cancellationToken: token, failedStartCleanup: cluster._failedStarts.Add))
                {
                    cluster._servers.Add(seed);
                    cluster.Tenant = await ReplicationTenant.CreateAsync(seed, ["chaos"], token);
                    key = await cluster.Tenant.KeyAsync(seed, ["documents:read", "documents:write"], token);
                    using var client = cluster._tls.CreateClient(seed.ClientAddress!, key);
                    using var collection = await client.PostAsJsonAsync(new Uri($"{cluster.Path}/collections", UriKind.Relative), new { name = "items" }, token);
                    collection.EnsureSuccessStatusCode();
                }

                for (var index = 0; index < 2; index++)
                {
                    var values = cluster.Settings(index, ports[index]);
                    var clock = cluster.Clocks[index];
                    var node = await TerraBaseTestServer.StartAsync(settings =>
                    {
                        foreach (var (name, value) in values)
                        {
                            settings[name] = value;
                        }
                    }, services => services.AddSingleton<TimeProvider>(clock), cancellationToken: token, failedStartCleanup: cluster._failedStarts.Add);
                    cluster._servers.Add(node);
                    cluster.Nodes[index] = node;
                    await cluster.Tenant.BindAsync(node);
                    cluster.Clients.Add(cluster._tls.CreateClient(node.ClientAddress!, await cluster.Tenant.KeyAsync(node, ["documents:read", "documents:write"], token)));
                }

                cluster.Child = await CrashableTerraBase.StartAsync(childSettings, token, cluster._failedStarts.Add);
                cluster.Clients.Add(cluster._tls.CreateClient(cluster.Child.ClientAddress, key));
                await cluster.ReplicatedAsync(token);
                return cluster;
            }
            catch (Exception startupError)
            {
                try
                {
                    await cluster.DisposeAsync();
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(startupError, cleanupError).Flatten();
                }

                throw;
            }
        }

        private Dictionary<string, string> Settings(int index, int port)
        {
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            _peers.Apply(settings);
            _tls.Apply(settings);
            settings["data-directory"] = System.IO.Path.Combine(_directory, index.ToString(CultureInfo.InvariantCulture));
            settings["region"] = "test-region";
            settings["node-name"] = $"chaos-{index}";
            settings["storage-threads"] = "1";
            settings["write-wait-ms"] = "0";
            settings["peer-port"] = port.ToString(CultureInfo.InvariantCulture);
            settings["peer-addresses"] = string.Join(',', Enumerable.Range(0, 3).Where(other => other != index).Select(other => _links[index, other]!.Address));
            return settings;
        }

        public Task ObserveNextBatch(int from, int to) => _links[from, to]!.ObserveNextBatch();

        public Task<int[]> IsolateAsync(int node, CancellationToken token) => Task.WhenAll(Enumerable.Range(0, 3).Where(other => other != node)
            .SelectMany(other => new[] { _links[node, other]!.CutAsync(token), _links[other, node]!.CutAsync(token) }));

        public void Heal(int node)
        {
            foreach (var other in Enumerable.Range(0, 3).Where(other => other != node))
            {
                _links[node, other]!.Heal();
                _links[other, node]!.Heal();
            }
        }

        public Task ReplicatedAsync(CancellationToken token) => TerraBaseTestServer.EventuallyAsync(async cancellation =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation);
            foreach (var client in Nodes.Select(node => node!.Client).Append(Child!.Client))
            {
                var scrape = await client.GetStringAsync(new Uri("/metrics", UriKind.Relative), linked.Token);
                if (Sample("terrabase_replication_sender_initialized") != 1 || Sample("terrabase_replication_sender_durable_lag") != 0)
                {
                    return false;
                }

                double Sample(string name)
                {
                    var samples = scrape.Split('\n').Where(line => line.StartsWith(name + " ", StringComparison.Ordinal)
                        || line.StartsWith(name + "{", StringComparison.Ordinal)).ToArray();
                    if (samples.Length == 0)
                    {
                        throw new InvalidOperationException($"chaos barrier has no {name} sample");
                    }

                    return samples.Sum(line => double.Parse(line[(line.Contains('}', StringComparison.Ordinal) ? line.LastIndexOf('}') + 1 : name.Length)..]
                        .Trim().Split(' ')[0], CultureInfo.InvariantCulture));
                }
            }

            return true;
        }, TimeSpan.FromSeconds(60));

        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>();
            foreach (var client in Clients)
            {
                Attempt(client.Dispose);
            }

            if (Child is not null)
            {
                await AttemptAsync(Child.DisposeAsync);
            }

            foreach (var node in Nodes.OfType<TerraBaseTestServer>())
            {
                await AttemptAsync(node.DisposeAsync);
            }

            foreach (var link in _links.OfType<PartitionablePeer>())
            {
                await AttemptAsync(link.DisposeAsync);
            }

            Attempt(() => ReleaseAfterCleanup(Task.WhenAll(_servers.Select(server => server.CleanupCompletion).Concat(_failedStarts)
                .Append(Child?.CleanupCompletion ?? Task.CompletedTask)
                .Concat(_links.OfType<PartitionablePeer>().Select(link => link.CleanupCompletion))), ReleaseFixtures));
            if (errors.Count > 0)
            {
                throw new AggregateException("chaos cluster cleanup failed", errors);
            }

            void ReleaseFixtures()
            {
                var failures = new List<Exception>();
                foreach (var release in new Action[] { _peers.Dispose, _tls.Dispose, () => Directory.Delete(_directory, recursive: true) })
                {
                    try
                    {
                        release();
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }

                if (failures.Count > 0)
                {
                    throw new AggregateException("chaos fixture cleanup failed", failures);
                }
            }

            async Task AttemptAsync(Func<ValueTask> dispose)
            {
                try
                {
                    await dispose();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }

            void Attempt(Action dispose)
            {
                try
                {
                    dispose();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
        }
    }
}
