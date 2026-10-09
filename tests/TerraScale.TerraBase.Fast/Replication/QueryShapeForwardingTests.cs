using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Fast.Management;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class QueryShapeForwardingTests
{
    private static readonly string[] EmailPredicate = ["/email", "private-forwarded-value"];
    [Test]
    public async Task Follower_queries_forward_an_immutable_batch_retry_without_double_counting_and_drain_the_tail_on_shutdown()
    {
        using var peers = new PeerTls();
        var time = new ManualFlushClock();
        var credential = string.Empty;
        await using var primary = await ManagementApi.StartAsync(settings =>
        {
            peers.Apply(settings);
            credential = settings["management-credential-file"];
        }, time: time, managementCredential: true);
        var database = await primary.CreateManagedDatabaseAsync();
        var key = await primary.Server.IssueAsync(database.Organization, "documents:read", "documents:write");
        TerraBaseTestServer? follower = await StartFollowerAsync(primary, peers, time, credential, services => services
            .AddSingleton<CatalogPrimaryClient>()
            .AddSingleton<LostShapeAcknowledgement>()
            .AddSingleton<ICatalogPrimaryClient>(provider => provider.GetRequiredService<LostShapeAcknowledgement>()));
        try
        {
            using var data = primary.Tls.CreateClient(follower.ClientAddress!, key);
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var response = await data.GetAsync(ManagementApi.Uri(database.Data + "/collections"), token);
                return response.StatusCode == HttpStatusCode.OK;
            });
            await SeedAsync(data, database);
            await QueryAsync(data, database, 1);
            using var forwarded = primary.Tls.CreateClient(follower.ClientAddress!, primary.Issuer.Mint(primary.User));
            using var local = new HttpClient { BaseAddress = follower.BaseAddress };
            local.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ManagementApi.ServiceCredential);
            local.DefaultRequestHeaders.Add("X-TerraBase-User-Id", primary.User);
            await Assert.That((await ShapesAsync(forwarded, database)).Length).IsEqualTo(0);
            await Assert.That((await ShapesAsync(local, database)).Length).IsEqualTo(0);

            var flush = follower.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single();
            var sent = follower.Services.GetRequiredService<LostShapeAcknowledgement>();
            var first = flush.FlushAsync(CancellationToken.None);
            try
            {
                await sent.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await QueryAsync(data, database, 2);
            }
            finally
            {
                sent.Release.TrySetResult();
            }
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That((await ShapesAsync(forwarded, database)).Single().Queries).IsEqualTo(1);
            // Replicated totals already contain the unacknowledged batch: a local administrative read must not add pending again.
            await TerraBaseTestServer.EventuallyAsync(async token =>
                (await ShapesAsync(local, database, token)).SingleOrDefault()?.Queries == 1);

            await flush.FlushAsync(CancellationToken.None);
            await flush.FlushAsync(CancellationToken.None);
            var batches = sent.Batches.ToArray();
            await Assert.That(batches.Select(batch => batch.Sequence)).IsEquivalentTo([1UL, 1UL, 2UL], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(batches[0].ToByteArray().SequenceEqual(batches[1].ToByteArray())).IsTrue();
            var stats = (await ShapesAsync(forwarded, database)).Single();
            await Assert.That(stats.Queries).IsEqualTo(2);
            await Assert.That(stats.RowsScanned).IsEqualTo(2);
            await Assert.That(stats.Shape).DoesNotContain("private-forwarded-value");

            await QueryAsync(data, database, 3);
            await follower.DisposeAsync();
            follower = null;
            await Assert.That((await ShapesAsync(primary.Client, database)).Single().Queries).IsEqualTo(3);
        }
        finally
        {
            if (follower is not null)
            {
                follower.Services.GetRequiredService<LostShapeAcknowledgement>().Release.TrySetResult();
                await follower.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task Rpc_acknowledges_only_after_commit_and_a_commit_that_throws_is_deduplicated_even_after_primary_restart()
    {
        using var peers = new PeerTls();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var hook = new UsageCommitHook((collection, _, _) => collection.EndsWith("/query_shapes", StringComparison.Ordinal));
        var directory = Directory.CreateTempSubdirectory("terrabase-shape-senders-").FullName;
        try
        {
            ManagedDatabaseRef database;
            string user;
            QueryShapesBatch uncertain;
            await using (var api = await ManagementApi.StartAsync(settings =>
            {
                peers.Apply(settings);
                settings["data-directory"] = directory;
            }, hook.Install, time))
            {
                user = api.User;
                database = await api.CreateManagedDatabaseAsync();
                var shape = await SavedShapeAsync(api, database);
                var payload = Payload(database, shape, time);
                using var channel = peers.CreateChannel(api.Server.PeerAddress!);
                var client = new CatalogQueryShapes.CatalogQueryShapesClient(channel);
                var first = Batch(payload, ByteString.CopyFrom(Guid.NewGuid().ToByteArray()));
                using (var gate = hook.PauseCommit())
                {
                    var pending = client.AddAsync(first).ResponseAsync;
                    try
                    {
                        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await Assert.That(pending.IsCompleted).IsFalse();
                        await Assert.That(await PersistedQueriesAsync(api.Server, database)).IsEqualTo(1);
                    }
                    finally
                    {
                        gate.Dispose();
                    }
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                }

                await Assert.That((await ShapesAsync(api.Client, database)).Single().Queries).IsEqualTo(2);
                uncertain = first.Clone();
                uncertain.Sequence = 2;
                using (var gate = hook.PauseAfterCommit(fail: true))
                {
                    var pending = client.AddAsync(uncertain).ResponseAsync;
                    try
                    {
                        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await Assert.That(pending.IsCompleted).IsFalse();
                        await Assert.That(await PersistedQueriesAsync(api.Server, database)).IsEqualTo(3);
                    }
                    finally
                    {
                        gate.Dispose();
                    }
                    await Assert.ThrowsAsync<RpcException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
                }
                await client.AddAsync(uncertain);
                await Assert.That((await ShapesAsync(api.Client, database)).Single().Queries).IsEqualTo(3);
            }

            await using (var api = await ManagementApi.StartAsync(settings =>
            {
                peers.Apply(settings);
                settings["data-directory"] = directory;
            }, time: time))
            {
                using var owner = api.As(user);
                using var channel = peers.CreateChannel(api.Server.PeerAddress!);
                var client = new CatalogQueryShapes.CatalogQueryShapesClient(channel);
                await client.AddAsync(uncertain);
                await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(3);
                var next = uncertain.Clone();
                next.Sequence = 3;
                await client.AddAsync(next);
                await client.AddAsync(uncertain);
                await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(4);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Forwarded_shapes_never_restore_deleted_state_cross_owners_or_replace_an_unreadable_record()
    {
        using var peers = new PeerTls();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(peers.Apply, time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var shape = await SavedShapeAsync(api, database);
        var other = await api.CreateManagedDatabaseAsync();
        var mismatched = await api.CreateManagedDatabaseAsync();
        var mismatchedShape = await SavedShapeAsync(api, mismatched);
        var deleted = await api.CreateManagedDatabaseAsync();
        await SavedShapeAsync(api, deleted);
        using (var response = await api.Client.DeleteAsync(ManagementApi.Uri(deleted.Path)))
        {
            response.EnsureSuccessStatusCode();
        }
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        var records = SystemCatalog.DatabaseState(database.Id) + "query_shapes";
        var id = shape.Collection + ":" + shape.Id;
        var unreadable = Encoding.UTF8.GetBytes("{\"format\":\"future\",\"stats\":[]}");
        await catalog.WriteAsync(async transaction =>
            await transaction.PutAsync(records, id, unreadable, CancellationToken.None), CancellationToken.None);
        var transfer = Transfer(database, shape);
        var payload = new QueryShapeForwardPayload([
            transfer,
            Transfer(mismatched, mismatchedShape) with { OrganizationId = other.Organization },
            Transfer(deleted, shape),
            Transfer(other, shape),
        ], time.GetUtcNow());
        using var channel = peers.CreateChannel(api.Server.PeerAddress!);
        var client = new CatalogQueryShapes.CatalogQueryShapesClient(channel);
        var batch = Batch(payload, ByteString.CopyFrom(Guid.NewGuid().ToByteArray()));
        await client.AddAsync(batch);
        await client.AddAsync(batch);
        await Assert.That((await ShapesAsync(api.Client, other)).Single().Queries).IsEqualTo(1);
        await Assert.That((await ShapesAsync(api.Client, mismatched)).Single().Queries).IsEqualTo(1);
        using (var unavailable = await api.Client.GetAsync(ManagementApi.Uri(StatsPath(database))))
        {
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }
        var (saved, removed, count) = await catalog.ReadAsync(async transaction =>
        {
            var stored = (await transaction.GetAsync(records, id, CancellationToken.None))!.Json.ToArray();
            var gone = await transaction.CountDocumentsAsync(SystemCatalog.DatabaseState(deleted.Id) + "query_shapes", CancellationToken.None);
            return (stored, gone, await transaction.CountDocumentsAsync(records, CancellationToken.None));
        }, CancellationToken.None);
        await Assert.That(saved.SequenceEqual(unreadable)).IsTrue();
        await Assert.That(removed).IsEqualTo(0);
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task Real_rpc_rejects_invalid_metadata_counts_envelopes_and_encoded_or_decoded_byte_limits_atomically()
    {
        using var peers = new PeerTls();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(peers.Apply, time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var shape = await SavedShapeAsync(api, database);
        var payload = Payload(database, shape, time);
        var valid = Batch(payload, ByteString.CopyFrom(Guid.NewGuid().ToByteArray()));
        var invalid = new List<QueryShapesBatch>();
        foreach (var change in new Action<QueryShapesBatch>[]
        {
            batch => batch.Format = "future",
            batch => batch.Epoch = ByteString.CopyFromUtf8("short"),
            batch => batch.Sender = string.Empty,
            batch => batch.Sender = new string('x', 129),
            batch => batch.Sequence = 0,
            batch => batch.Sequence = (ulong)long.MaxValue + 1,
            batch => batch.Envelope = ByteString.Empty,
        })
        {
            var batch = valid.Clone();
            change(batch);
            invalid.Add(batch);
        }
        invalid.Add(Batch(new QueryShapeForwardPayload([], time.GetUtcNow()), valid.Epoch));
        invalid.Add(Batch(new QueryShapeForwardPayload([.. Enumerable.Repeat(Transfer(database, shape), 129)], time.GetUtcNow()), valid.Epoch));
        // Each array individually fits the cap. Duplicate Web-default property names must fail before DTO materialization.
        var repeated = JsonSerializer.Serialize(new QueryShapeForwardPayload(
            [.. Enumerable.Repeat(Transfer(database, shape), QueryShapeForwarding.MaxItems)], time.GetUtcNow()),
            TerraBaseJsonContext.Default.QueryShapeForwardPayload);
        using (var document = JsonDocument.Parse(repeated))
        {
            var items = document.RootElement.GetProperty("items").GetRawText();
            foreach (var name in new[] { "items", "Items", "it\\u0065ms" })
            {
                var json = Encoding.UTF8.GetBytes(repeated.Replace("\"items\":", $"\"items\":{items},\"{name}\":", StringComparison.Ordinal));
                await Assert.That(json.Length).IsLessThan(QueryShapeForwarding.MaxBytes);
                var duplicate = valid.Clone();
                duplicate.Envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(json).Span);
                invalid.Add(duplicate);
            }
        }
        invalid.Add(Batch(new QueryShapeForwardPayload([Transfer(database, shape), Transfer(database, shape with { Queries = -1 })], time.GetUtcNow()), valid.Epoch));
        invalid.Add(Batch(new QueryShapeForwardPayload([Transfer(database, shape) with { OrganizationId = "not-a-uuid" }], time.GetUtcNow()), valid.Epoch));
        invalid.Add(Batch(new QueryShapeForwardPayload([Transfer(database, shape) with { ProjectId = string.Empty }], time.GetUtcNow()), valid.Epoch));
        invalid.Add(Batch(payload with { FirstSentAt = time.GetUtcNow().AddDays(-2) }, valid.Epoch));
        var tampered = valid.Clone();
        var corrupt = valid.Envelope.ToByteArray();
        corrupt[^1] ^= 1;
        tampered.Envelope = ByteString.CopyFrom(corrupt);
        invalid.Add(tampered);
        var decoded = valid.Clone();
        decoded.Envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(new byte[(1024 * 1024) + 1]).Span);
        invalid.Add(decoded);
        var encoded = valid.Clone();
        // A valid LZ4 envelope whose decoded bytes fit, but whose incompressible block exceeds the encoded cap.
        var tooLarge = ApplicationEnvelope.Encode(RandomNumberGenerator.GetBytes(QueryShapeForwarding.MaxBytes - 4000));
        await Assert.That(tooLarge.Length).IsGreaterThan(QueryShapeForwarding.MaxBytes);
        await Assert.That(tooLarge.Length).IsLessThanOrEqualTo(QueryShapeForwarding.MaxMessageBytes - 256);
        encoded.Envelope = ByteString.CopyFrom(tooLarge.Span);
        invalid.Add(encoded);
        using var channel = peers.CreateChannel(api.Server.PeerAddress!);
        var client = new CatalogQueryShapes.CatalogQueryShapesClient(channel);
        foreach (var batch in invalid)
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await client.AddAsync(batch));
            await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
        await Assert.That((await ShapesAsync(api.Client, database)).Single().Queries).IsEqualTo(1);
        // None of the refusals consumed sequence 1 or left the receive admission held.
        await client.AddAsync(valid);
        await Assert.That((await ShapesAsync(api.Client, database)).Single().Queries).IsEqualTo(2);

        await using var follower = await StartFollowerAsync(api, peers, time);
        using var followerChannel = peers.CreateChannel(follower.PeerAddress!);
        var notPrimary = await Assert.ThrowsAsync<RpcException>(async () =>
            await new CatalogQueryShapes.CatalogQueryShapesClient(followerChannel).AddAsync(valid));
        await Assert.That(notPrimary!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        var primary = follower.Services.GetRequiredService<ICatalogPrimaryClient>();
        await Assert.That(await primary.AddQueryShapesAsync(invalid[0], CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task Rpc_admits_at_most_four_receives_releases_their_memory_and_retries_after_memory_rejection()
    {
        using var peers = new PeerTls();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var hook = new UsageCommitHook((collection, _, _) => collection.EndsWith("/query_shapes", StringComparison.Ordinal));
        await using var api = await ManagementApi.StartAsync(peers.Apply, services =>
        {
            hook.Install(services);
            var registration = services.Single(service => service.ServiceType == typeof(IMemoryBudget));
            services.Remove(registration);
            services.AddSingleton(provider => new ReceiveMemory((IMemoryBudget)registration.ImplementationFactory!(provider)));
            services.AddSingleton<IMemoryBudget>(provider => provider.GetRequiredService<ReceiveMemory>());
        }, time);
        var database = await api.CreateManagedDatabaseAsync();
        var shape = await SavedShapeAsync(api, database);
        var payload = Payload(database, shape, time);
        var memory = api.Server.Services.GetRequiredService<ReceiveMemory>();
        using var channel = peers.CreateChannel(api.Server.PeerAddress!);
        var client = new CatalogQueryShapes.CatalogQueryShapesClient(channel);
        var pending = new List<Task<QueryShapesAck>>();
        using (var gate = hook.PauseCommit())
        {
            try
            {
                pending.Add(client.AddAsync(Batch(payload, ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), sender: "first")).ResponseAsync);
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                for (var i = 1; i < 4; i++)
                {
                    pending.Add(client.AddAsync(Batch(payload, ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), sender: "queued" + i)).ResponseAsync);
                }
                await memory.FourEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var refused = await Assert.ThrowsAsync<RpcException>(async () =>
                    await client.AddAsync(Batch(payload, ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), sender: "fifth")));
                await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
                await Assert.That(memory.Active).IsEqualTo(4);
            }
            finally
            {
                gate.Dispose();
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(memory.Active == 0));
        await Assert.That(memory.Active).IsEqualTo(0);
        await Assert.That((await ShapesAsync(api.Client, database)).Single().Queries).IsEqualTo(5);
        var retry = Batch(payload, ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), sender: "memory");
        memory.Refuse = true;
        try
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await client.AddAsync(retry));
            await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
            await Assert.That(memory.Active).IsEqualTo(0);
            await Assert.That((await ShapesAsync(api.Client, database)).Single().Queries).IsEqualTo(5);
        }
        finally
        {
            memory.Refuse = false;
        }
        await client.AddAsync(retry);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(memory.Active == 0));
        await Assert.That(memory.Active).IsEqualTo(0);
        await Assert.That((await ShapesAsync(api.Client, database)).Single().Queries).IsEqualTo(6);
    }

    private static Task<TerraBaseTestServer> StartFollowerAsync(
        ManagementApi primary, PeerTls peers, TimeProvider time, string? credential = null, Action<IServiceCollection>? services = null) =>
        TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            primary.Tls.Apply(settings);
            TestIssuer.Configure(settings);
            settings["catalog-primary"] = primary.Server.PeerAddress!.ToString();
            if (credential is not null)
            {
                settings["management-credential-file"] = credential;
            }
        }, collection =>
        {
            primary.Issuer.Route(collection);
            collection.AddSingleton<TimeProvider>(time);
            services?.Invoke(collection);
        });

    private static async Task SeedAsync(HttpClient data, ManagedDatabaseRef database)
    {
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var seeded = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "one")),
            new { n = 1, email = new Faker().Internet.Email() });
        seeded.EnsureSuccessStatusCode();
    }

    private static async Task QueryAsync(HttpClient data, ManagedDatabaseRef database, int number)
    {
        using var response = await data.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/collections/people/query"), new
        {
            filter = new { and = new object[] { new { eq = new object[] { "/n", number } }, new { eq = EmailPredicate } } },
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<QueryShapeStats> SavedShapeAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SeedAsync(data, database);
        await QueryAsync(data, database, 1);
        await api.Server.Services.GetRequiredService<QueryShapeRecorder>().FlushAsync(CancellationToken.None);
        return (await ShapesAsync(api.Client, database)).Single();
    }

    private static string StatsPath(ManagedDatabaseRef database) => database.Path[3..] + "/query-shapes";

    // Read persisted state without the recorder's semaphore while the receive deliberately holds its commit gate.
    private static async Task<long> PersistedQueriesAsync(TerraBaseTestServer server, ManagedDatabaseRef database) =>
        await server.Services.GetRequiredService<SystemCatalog>().ReadAsync(async transaction =>
        {
            var saved = await SystemCatalog.ListAllAsync(transaction, SystemCatalog.DatabaseState(database.Id) + "query_shapes",
                TerraBaseJsonContext.Default.QueryShapeRecord, CancellationToken.None);
            return saved.Single().Stats.Queries;
        }, CancellationToken.None);

    private static async Task<QueryShapeStats[]> ShapesAsync(HttpClient client, ManagedDatabaseRef database, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(ManagementApi.Uri(StatsPath(database)), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return [.. json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Deserialize(TerraBaseJsonContext.Default.QueryShapeStats)!)];
    }

    private static QueryShapeTransfer Transfer(ManagedDatabaseRef database, QueryShapeStats stats) =>
        new(database.Organization, database.Project, database.Id, stats);

    private static QueryShapeForwardPayload Payload(ManagedDatabaseRef database, QueryShapeStats stats, TimeProvider time) =>
        new([Transfer(database, stats)], time.GetUtcNow());

    private static QueryShapesBatch Batch(QueryShapeForwardPayload payload, ByteString epoch, ulong sequence = 1, string sender = "test") => new()
    {
        Format = "alpha",
        Epoch = epoch,
        Sender = sender,
        Sequence = sequence,
        Envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(JsonSerializer.SerializeToUtf8Bytes(payload, TerraBaseJsonContext.Default.QueryShapeForwardPayload)).Span),
    };

    // Park periodic flushes while transport deadlines use current UTC and real timers.
    private sealed class ManualFlushClock : TimeProvider
    {
        private readonly FakeTimeProvider _periodic = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            period == Timeout.InfiniteTimeSpan
                ? base.CreateTimer(callback, state, dueTime, period)
                : _periodic.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class LostShapeAcknowledgement(CatalogPrimaryClient inner) : ICatalogPrimaryClient
    {
        public ConcurrentQueue<QueryShapesBatch> Batches { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> AddUsageAsync(UsageBatch batch, CancellationToken cancellationToken) => inner.AddUsageAsync(batch, cancellationToken);

        public async Task<bool> AddQueryShapesAsync(QueryShapesBatch batch, CancellationToken cancellationToken)
        {
            Batches.Enqueue(batch.Clone());
            var first = Batches.Count == 1;
            if (first)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            var added = await inner.AddQueryShapesAsync(batch, cancellationToken);
            if (first && added)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "the query-shape acknowledgement was lost"));
            }
            return added;
        }
    }

    private sealed class ReceiveMemory(IMemoryBudget inner) : IMemoryBudget
    {
        private int _active;
        public int Active => Volatile.Read(ref _active);
        public bool Refuse
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }
        public TaskCompletionSource FourEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes)
        {
            if (lane != MemoryLane.Replication || bytes != 8L * 1024 * 1024)
            {
                return inner.TryReserve(lane, bytes);
            }
            if (Refuse || inner.TryReserve(lane, bytes) is not { } reservation)
            {
                return null;
            }
            if (Interlocked.Increment(ref _active) == 4)
            {
                FourEntered.TrySetResult();
            }
            return new ReceiveReservation(this, reservation);
        }

        private sealed class ReceiveReservation(ReceiveMemory memory, IMemoryReservation inner) : IMemoryReservation
        {
            private int _disposed;
            public long Bytes => inner.Bytes;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    inner.Dispose();
                    Interlocked.Decrement(ref memory._active);
                }
            }
        }
    }
}
