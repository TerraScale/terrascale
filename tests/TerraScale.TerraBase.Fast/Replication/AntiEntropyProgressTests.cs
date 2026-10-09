using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Fast.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.Alpha;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class AntiEntropyProgressTests
{
    [Test]
    [Timeout(60_000)]
    public async Task Invalid_members_do_not_block_healthy_membership_and_configured_fallback_repairs(CancellationToken cancellationToken)
    {
        var token = cancellationToken;
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var members = new FakeMemberStore(clock);
        ProgressState? memberState = null;
        ProgressState? fallbackState = null;
        await using var member = await ReceiverAsync(state => memberState = state);
        await using var fallback = await ReceiverAsync(state => fallbackState = state);
        Console.WriteLine("Membership repair: receivers started; starting seed.");
        var directory = Directory.CreateTempSubdirectory("terrabase-membership-repair-").FullName;
        try
        {
            ReplicationTenant tenant;
            string[] expected;
            string[] sourceFrontiers;
            var id = DocumentApi.NewId();
            var name = new Faker().Name.FullName();
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["data-directory"] = directory;
                settings["peer-addresses"] = $"{member.PeerAddress},{fallback.PeerAddress}";
            }, services => services.AddSingleton<TimeProvider>(clock)))
            {
                Console.WriteLine("Membership repair: seed started; provisioning acknowledged state.");
                tenant = await ReplicationTenant.CreateAsync(seed, ["repair"], token);
                await tenant.BindAsync(member);
                await tenant.BindAsync(fallback);
                using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, ["documents:write"], token));
                using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative), new { name = "items" }, token))
                {
                    collection.EnsureSuccessStatusCode();
                }

                using var created = await writer.PutAsJsonAsync(
                    new Uri($"{tenant.Path("repair")}/collections/items/documents/by-key?id={id}", UriKind.Relative), new { name }, token);
                created.EnsureSuccessStatusCode();
                await TerraBaseTestServer.ReplicatedAsync(seed);

                expected = await StatesAsync(tenant.File(seed, "repair"), token);
                sourceFrontiers = await FrontiersAsync(tenant.File(seed, "repair"), token);
                Console.WriteLine("Membership repair: seed caught up; stopping seed.");
            }

            var receivers = new[] { member, fallback };
            var receiverFrontiers = new List<string[]>();
            foreach (var receiver in receivers)
            {
                var file = tenant.File(receiver, "repair");
                await Assert.That(await StatesAsync(file, token)).IsEquivalentTo(expected);
                receiverFrontiers.Add(await FrontiersAsync(file, token));
                // Leave the acknowledged journal intact: only maintenance can restore this stale row.
                await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file));
                await connection.OpenAsync(token);
                using var corrupt = connection.CreateCommand();
                corrupt.CommandText = "UPDATE documents SET version = 1, body = '{\"name\":\"stale\"}' WHERE collection = 'items';";
                await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(1);
            }

            await members.PutAsync(new ClusterMember("a-malformed", "https://[invalid", "test-region", ""), token);
            await members.PutAsync(new ClusterMember("b-relative", "/peer", "test-region", ""), token);
            await members.PutAsync(new ClusterMember("c-non-https", "http://localhost:1", "test-region", ""), token);
            await members.PutAsync(new ClusterMember("d-healthy", member.PeerAddress!.ToString(), "test-region", ""), token);
            var incomingMember = memberState ?? throw new InvalidOperationException("the member repair state was not registered");
            var incomingFallback = fallbackState ?? throw new InvalidOperationException("the fallback repair state was not registered");
            incomingMember.Begin.TrySetResult();
            incomingFallback.Begin.TrySetResult();
            // The member is deliberately absent from configured peers after the acknowledged seed is stopped.
            await using var source = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["node-name"] = "repair-source";
                settings["data-directory"] = directory;
                settings["peer-addresses"] = fallback.PeerAddress!.ToString();
            }, services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IMemberStore>(members);
            });
            Console.WriteLine("Membership repair: source started; waiting for both replicas to heal.");
            await Task.WhenAll(incomingMember.Repaired.Task, incomingFallback.Repaired.Task).WaitAsync(token);

            for (var index = 0; index < receivers.Length; index++)
            {
                var receiver = receivers[index];
                await Assert.That(await StatesAsync(tenant.File(receiver, "repair"), token)).IsEquivalentTo(expected);
                await Assert.That(await FrontiersAsync(tenant.File(receiver, "repair"), token)).IsEquivalentTo(receiverFrontiers[index]);
                using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, ["documents:read"], token));
                using var read = await reader.GetAsync(new Uri($"{tenant.Path("repair")}/collections/items/documents/by-key?id={id}", UriKind.Relative), token);
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
                await Assert.That(body.RootElement.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
            }

            await Assert.That(incomingMember.RepairedKeys).IsEqualTo(1);
            await Assert.That(incomingFallback.RepairedKeys).IsEqualTo(1);
            await Assert.That(await FrontiersAsync(tenant.File(source, "repair"), token)).IsEquivalentTo(sourceFrontiers);
            Console.WriteLine("Membership repair: repair assertions passed; stopping source.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Task<TerraBaseTestServer> ReceiverAsync(Action<ProgressState> capture)
        {
            return TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); }, services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IAntiEntropyState>(provider =>
                {
                    var state = new ProgressState(ActivatorUtilities.CreateInstance<AntiEntropyState>(provider), clock, null,
                        slow: false, stall: false, retry: null, expectedKeys: 1);
                    capture(state);
                    return state;
                });
            });
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Long_digests_and_progressing_pages_repair_across_multiple_progress_budgets(CancellationToken cancellationToken)
    {
        await AssertRepairAsync(stall: false, cancellationToken);
    }

    [Test]
    [Timeout(120_000)]
    public async Task A_stalled_range_is_cancelled_and_the_next_pass_repairs_it(CancellationToken cancellationToken)
    {
        await AssertRepairAsync(stall: true, cancellationToken);
    }

    [Test]
    [Timeout(120_000)]
    public async Task A_backward_range_cursor_aborts_the_exchange_and_the_next_pass_repairs_it(CancellationToken cancellationToken)
    {
        await AssertRepairAsync(stall: false, cancellationToken, backward: true);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Endless_advancing_pages_yield_to_the_next_database_and_retry_on_the_next_pass(CancellationToken cancellationToken)
    {
        var token = cancellationToken;
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var started = DateTimeOffset.UtcNow;
        var sourceClock = new RepairClock(started);
        var receiverClock = new FakeTimeProvider(started);
        ProgressState? receiverState = null;
        await using var receiver = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); }, services =>
        {
            services.AddSingleton<TimeProvider>(receiverClock);
            services.AddSingleton<IAntiEntropyState>(provider => receiverState = new ProgressState(
                ActivatorUtilities.CreateInstance<AntiEntropyState>(provider), receiverClock, sourceClock,
                slow: false, stall: false, retry: null, expectedKeys: 2));
        });
        await using var source = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        }, services => services.AddSingleton<TimeProvider>(sourceClock));
        await sourceClock.PausedAsync(token);

        string[] names = ["first", "second"];
        var tenant = await ReplicationTenant.CreateAsync(source, names, token);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], token));
        var id = DocumentApi.NewId();
        var name = new Faker().Name.FullName();
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var frontiers = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var database in names)
        {
            using var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path(database)}/collections", UriKind.Relative), new { name = "items" }, token);
            collection.EnsureSuccessStatusCode();
            using var document = await writer.PutAsJsonAsync(new Uri($"{tenant.Path(database)}/collections/items/documents/by-key?id={id}", UriKind.Relative), new { name }, token);
            document.EnsureSuccessStatusCode();
        }

        await TerraBaseTestServer.ReplicatedAsync(source);
        foreach (var database in names)
        {
            var file = tenant.File(receiver, database);
            expected.Add(database, await StatesAsync(tenant.File(source, database), token));
            await Assert.That(await StatesAsync(file, token)).IsEquivalentTo(expected[database]);
            frontiers.Add(database, await FrontiersAsync(file, token));
            await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file));
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE documents SET version = 1, body = '{\"name\":\"stale\"}' WHERE collection = 'items';";
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(1);
        }

        var incoming = receiverState ?? throw new InvalidOperationException("the receiver repair state was not registered");
        incoming.EndlessPages = true;
        incoming.Begin.TrySetResult();
        sourceClock.Advance(AntiEntropyWorker.ReconcileInterval);
        await incoming.EndlessCancelled.Task.WaitAsync(token);
        await sourceClock.PausedAsync(token);
        await Assert.That(incoming.EndlessReads).IsGreaterThan(1);
        await Assert.That(sourceClock.GetUtcNow() - started).IsEqualTo(AntiEntropyWorker.ReconcileInterval + AntiEntropyWorker.ExchangeTimeout);
        await Assert.That(incoming.RepairedKeys).IsEqualTo(1);
        foreach (var database in names)
        {
            var repaired = tenant.Database(database).DatabaseId != incoming.EndlessDatabase;
            await Assert.That((await StatesAsync(tenant.File(receiver, database), token)).SequenceEqual(expected[database])).IsEqualTo(repaired);
        }

        incoming.EndlessPages = false;
        sourceClock.Advance(AntiEntropyWorker.ReconcileInterval);
        await incoming.Repaired.Task.WaitAsync(token);
        await sourceClock.PausedAsync(token);
        await Assert.That(incoming.RepairedKeys).IsEqualTo(2);
        foreach (var database in names)
        {
            await Assert.That(await StatesAsync(tenant.File(receiver, database), token)).IsEquivalentTo(expected[database]);
            await Assert.That(await FrontiersAsync(tenant.File(receiver, database), token)).IsEquivalentTo(frontiers[database]);
        }
    }

    private static async Task AssertRepairAsync(bool stall, CancellationToken token, bool backward = false)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var started = DateTimeOffset.UtcNow;
        var sourceClock = new RepairClock(started);
        var receiverClock = new FakeTimeProvider(started);
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProgressState? sourceState = null;
        ProgressState? receiverState = null;
        await using var receiver = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); }, services =>
        {
            services.AddSingleton<TimeProvider>(receiverClock);
            services.AddSingleton<IAntiEntropyState>(provider => receiverState = new ProgressState(
                ActivatorUtilities.CreateInstance<AntiEntropyState>(provider), receiverClock, sourceClock,
                slow: !stall && !backward, stall, stall || backward ? retry : null, backward));
        });
        await using var source = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        }, services =>
        {
            services.AddSingleton<TimeProvider>(sourceClock);
            services.AddSingleton<IAntiEntropyState>(provider => sourceState = new ProgressState(
                ActivatorUtilities.CreateInstance<AntiEntropyState>(provider), sourceClock, null,
                slow: !stall && !backward, stall: false, stall || backward ? retry : null));
        });
        // The initial empty pass must finish before this fixture provisions its database.
        await sourceClock.PausedAsync(token);

        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], token);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], token));
        using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative), new { name = "items" }, token))
        {
            collection.EnsureSuccessStatusCode();
        }

        var ids = Enumerable.Range(0, 130).Select(_ => DocumentApi.NewId()).ToArray();
        var name = new Faker().Name.FullName();
        foreach (var batch in ids.Chunk(64))
        {
            using var transaction = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/transactions", UriKind.Relative),
                new { ops = batch.Select(id => new { op = "create", collection = "items", id, document = new { name } }).ToArray() }, token);
            transaction.EnsureSuccessStatusCode();
        }

        await TerraBaseTestServer.ReplicatedAsync(source);
        var sourceFile = tenant.File(source, "repair");
        var receiverFile = tenant.File(receiver, "repair");
        var expected = await StatesAsync(sourceFile, token);
        await Assert.That(await StatesAsync(receiverFile, token)).IsEquivalentTo(expected);
        var sourceFrontiers = await FrontiersAsync(sourceFile, token);
        var receiverFrontiers = await FrontiersAsync(receiverFile, token);
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(receiverFile)))
        {
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE documents SET version = 1, body = '{\"name\":\"stale\"}' WHERE collection = 'items';";
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(130);
        }

        var outgoing = sourceState ?? throw new InvalidOperationException("the source repair state was not registered");
        var incoming = receiverState ?? throw new InvalidOperationException("the receiver repair state was not registered");
        incoming.Begin.TrySetResult();
        outgoing.Begin.TrySetResult();
        sourceClock.Advance(AntiEntropyWorker.ReconcileInterval);
        if (stall)
        {
            // Advance only after the real range RPC is waiting, so neither transport startup nor disk scheduling counts as the stall.
            await incoming.StallEntered.Task.WaitAsync(token);
            receiverClock.Advance(TimeSpan.FromSeconds(16));
            sourceClock.Advance(TimeSpan.FromSeconds(16));
            await incoming.StallCancelled.Task.WaitAsync(token);
            await sourceClock.PausedAsync(token);
            await AdvanceIdleAsync(expectedRepaired: 0);
            await outgoing.RetryEntered.Task.WaitAsync(token);
            retry.TrySetResult();
        }

        if (backward)
        {
            await incoming.BackwardSent.Task.WaitAsync(token);
            await sourceClock.PausedAsync(token);
            await AdvanceIdleAsync(expectedRepaired: 0);
            // The next digest proves the malformed exchange ended before any repair was pushed.
            await outgoing.RetryEntered.Task.WaitAsync(token);
            await Assert.That(incoming.RepairedKeys).IsEqualTo(0);
            retry.TrySetResult();
        }

        await incoming.Repaired.Task.WaitAsync(token);
        await sourceClock.PausedAsync(token);
        await Assert.That(await StatesAsync(receiverFile, token)).IsEquivalentTo(expected);
        await Assert.That(incoming.RepairedKeys).IsEqualTo(130);
        await Assert.That(outgoing.Cancellations).IsEqualTo(0);
        await Assert.That(incoming.Cancellations).IsEqualTo(stall ? 1 : 0);
        if (!stall && !backward)
        {
            await Assert.That(outgoing.DigestCalls).IsEqualTo(1);
            await Assert.That(incoming.DigestCalls).IsEqualTo(1);
            await Assert.That(outgoing.ReadCalls).IsEqualTo(2);
            await Assert.That(incoming.ReadCalls).IsEqualTo(2);
            await Assert.That(outgoing.MergeCalls).IsEqualTo(2);
            await Assert.That(incoming.MergeCalls).IsEqualTo(2);
            await Assert.That(sourceClock.GetUtcNow() - started).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(88));
            await Assert.That(receiverClock.GetUtcNow() - started).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(44));
        }

        using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, ["documents:read"], token));
        foreach (var id in new[] { ids[0], ids[^1] })
        {
            using var read = await reader.GetAsync(new Uri($"{tenant.Path("repair")}/collections/items/documents/by-key?id={id}", UriKind.Relative), token);
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
            await Assert.That(body.RootElement.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        }

        await Assert.That(await FrontiersAsync(sourceFile, token)).IsEquivalentTo(sourceFrontiers);
        await Assert.That(await FrontiersAsync(receiverFile, token)).IsEquivalentTo(receiverFrontiers);

        // A successful pass that overran the interval still rests before scanning unchanged journal heads again.
        var completedDigests = outgoing.DigestCalls;
        await AdvanceIdleAsync(expectedRepaired: 130);
        await sourceClock.PausedAsync(token);
        await Assert.That(outgoing.DigestCalls).IsEqualTo(completedDigests + 1);

        async Task AdvanceIdleAsync(long expectedRepaired)
        {
            var calls = outgoing.DigestCalls;
            await Assert.That(incoming.RepairedKeys).IsEqualTo(expectedRepaired);
            var remaining = TimeSpan.FromMilliseconds(1);
            sourceClock.Advance(AntiEntropyWorker.ReconcileInterval - remaining);
            await Assert.That(outgoing.DigestCalls).IsEqualTo(calls);
            sourceClock.Advance(remaining);
        }
    }

    private static Task<string[]> StatesAsync(string file, CancellationToken token) =>
        RowsAsync(file, "SELECT id || ':' || version || ':' || body FROM documents WHERE collection = 'items';", token);

    private static Task<string[]> FrontiersAsync(string file, CancellationToken token) => RowsAsync(file, """
        SELECT 'journal:' || sequence || ':' || hex(digest) FROM journal_prefixes
        UNION ALL SELECT 'applied:' || hex(origin) || ':' || sequence || ':' || hex(digest) FROM applied;
        """, token);

    private static async Task<string[]> RowsAsync(string file, string sql, CancellationToken token)
    {
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file, readOnly: true));
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var rows = await command.ExecuteReaderAsync(token);
        var values = new List<string>();
        while (await rows.ReadAsync(token))
        {
            values.Add(rows.GetString(0));
        }

        return [.. values];
    }

    // The real state completes its disk work first; advancing time then measures the surrounding worker/RPC budget.
    private sealed class ProgressState(IAntiEntropyState inner, FakeTimeProvider clock, FakeTimeProvider? callerClock,
        bool slow, bool stall, TaskCompletionSource? retry, bool backward = false, int expectedKeys = 130) : IAntiEntropyState
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _digests;
        private int _reads;
        private int _merges;
        private int _cancellations;
        private int _stalled;
        private int _endlessReads;
        private string? _endlessDatabase;
        private long _repaired;

        public TaskCompletionSource Begin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StallCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BackwardSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RetryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Repaired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EndlessCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool EndlessPages { get; set; }
        public string? EndlessDatabase => Volatile.Read(ref _endlessDatabase);
        public int EndlessReads => Volatile.Read(ref _endlessReads);
        public int DigestCalls => Volatile.Read(ref _digests);
        public int ReadCalls => Volatile.Read(ref _reads);
        public int MergeCalls => Volatile.Read(ref _merges);
        public int Cancellations => Volatile.Read(ref _cancellations);
        public long RepairedKeys => Interlocked.Read(ref _repaired);

        public ValueTask<IDatabaseStore> OpenAsync(ShardIdentity identity, CancellationToken cancellationToken) => inner.OpenAsync(identity, cancellationToken);

        public async Task<byte[]> DigestAsync(IDatabaseStore store, CancellationToken cancellationToken)
        {
            try
            {
                await Begin.Task.WaitAsync(cancellationToken);
                if (EndlessPages)
                {
                    Interlocked.CompareExchange(ref _endlessDatabase, store.Tenant!.DatabaseId, null);
                }

                var attempt = Interlocked.Increment(ref _digests);
                if (attempt > 1 && retry is not null)
                {
                    RetryEntered.TrySetResult();
                    await retry.Task.WaitAsync(cancellationToken);
                }

                var digest = await inner.DigestAsync(store, cancellationToken);
                if (slow && attempt == 1)
                {
                    Advance(TimeSpan.FromSeconds(20), cancellationToken);
                }

                return digest;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancellations);
                throw;
            }
        }

        public async Task<StatePage> ReadAsync(IDatabaseStore store, RangeRequest request, CancellationToken cancellationToken)
        {
            try
            {
                if (EndlessPages && request.Identity.Database == EndlessDatabase)
                {
                    var endlessAttempt = Interlocked.Increment(ref _endlessReads);
                    callerClock!.Advance(TimeSpan.FromSeconds(10));
                    if (TimeSpan.FromSeconds(endlessAttempt * 10) >= AntiEntropyWorker.ExchangeTimeout)
                    {
                        try
                        {
                            await _never.Task.WaitAsync(cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            EndlessCancelled.TrySetResult();
                            throw;
                        }
                    }

                    return new StatePage
                    {
                        HasMore = true,
                        AfterCollection = "items",
                        AfterId = endlessAttempt.ToString("D4", CultureInfo.InvariantCulture),
                    };
                }

                if (stall && Interlocked.Exchange(ref _stalled, 1) == 0)
                {
                    StallEntered.TrySetResult();
                    try
                    {
                        await _never.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        StallCancelled.TrySetResult();
                        throw;
                    }
                }

                var page = await inner.ReadAsync(store, request, cancellationToken);
                var attempt = Interlocked.Increment(ref _reads);
                if (backward && attempt == 2)
                {
                    page.HasMore = true;
                    page.AfterCollection = request.AfterCollection;
                    page.AfterId = Guid.Empty.ToString("D");
                    BackwardSent.TrySetResult();
                }

                if (slow && attempt <= 2)
                {
                    Advance(TimeSpan.FromSeconds(6), cancellationToken);
                }

                return page;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancellations);
                throw;
            }
        }

        public async Task<uint> MergeAsync(IDatabaseStore store, ShardIdentity identity, ByteString envelope, CancellationToken cancellationToken)
        {
            try
            {
                var repaired = await inner.MergeAsync(store, identity, envelope, cancellationToken);
                var attempt = Interlocked.Increment(ref _merges);
                if (slow && attempt <= 2)
                {
                    Advance(TimeSpan.FromSeconds(6), cancellationToken);
                }

                if (Interlocked.Add(ref _repaired, repaired) >= expectedKeys)
                {
                    Repaired.TrySetResult();
                }

                return repaired;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancellations);
                throw;
            }
        }

        private void Advance(TimeSpan duration, CancellationToken token)
        {
            clock.Advance(duration);
            callerClock?.Advance(duration);
            token.ThrowIfCancellationRequested();
        }
    }
}
