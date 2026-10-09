using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class QueryShapeForwardingLifetimeTests
{
    private static readonly string[] EmailPredicate = ["/email", "private-lifetime-value"];

    [Test]
    public async Task One_day_expiry_drops_the_ambiguous_snapshot_and_forwards_observations_arriving_before_its_acknowledgement()
    {
        using var peers = new PeerTls();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var credential = string.Empty;
        await using var primary = await ManagementApi.StartAsync(settings =>
        {
            peers.Apply(settings);
            credential = settings["management-credential-file"];
        }, time: time, managementCredential: true);
        var database = await primary.CreateManagedDatabaseAsync();
        var key = await primary.Server.IssueAsync(database.Organization, "documents:read", "documents:write");
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            primary.Tls.Apply(settings);
            TestIssuer.Configure(settings);
            settings["catalog-primary"] = primary.Server.PeerAddress!.ToString();
            settings["management-credential-file"] = credential;
        }, services =>
        {
            primary.Issuer.Route(services);
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<CatalogPrimaryClient>();
            services.AddSingleton<AmbiguousShapeAcknowledgement>();
            services.AddSingleton<ICatalogPrimaryClient>(provider => provider.GetRequiredService<AmbiguousShapeAcknowledgement>());
        });
        var flush = follower.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single();
        using var data = primary.Tls.CreateClient(follower.ClientAddress!, key);
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await data.GetAsync(ManagementApi.Uri(database.Data + "/collections"), token);
            return response.StatusCode == HttpStatusCode.OK;
        });
        await SeedAsync(data, database);
        await QueryAsync(data, database, 1);
        var sender = follower.Services.GetRequiredService<AmbiguousShapeAcknowledgement>();
        var first = flush.FlushAsync(CancellationToken.None);
        try
        {
            await sender.Committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // This observation arrives while the first immutable snapshot is waiting for an acknowledgement.
            await QueryAsync(data, database, 2);
        }
        finally
        {
            sender.Release.TrySetResult();
        }
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        using var owner = primary.Service(primary.User);
        await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(1);

        time.Advance(TimeSpan.FromDays(1));
        await flush.FlushAsync(CancellationToken.None);
        await flush.FlushAsync(CancellationToken.None);
        var batches = sender.Batches.ToArray();
        await Assert.That(batches.Select(batch => batch.Sequence)).IsEquivalentTo([1UL, 2UL], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(batches[0].Epoch).IsEqualTo(batches[1].Epoch);
        QueryShapeForwardPayload[] snapshots = [.. batches.Select(batch => JsonSerializer.Deserialize(
            ApplicationEnvelope.Decode(batch.Envelope.Span, 1024 * 1024), TerraBaseJsonContext.Default.QueryShapeForwardPayload)!)];
        await Assert.That(snapshots[1].FirstSentAt - snapshots[0].FirstSentAt).IsEqualTo(TimeSpan.FromDays(1));
        await Assert.That(snapshots.Select(snapshot => snapshot.Items.Single().Stats.Queries))
            .IsEquivalentTo([1L, 1L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var stats = (await ShapesAsync(owner, database)).Single();
        await Assert.That(stats.Queries).IsEqualTo(2);
        await Assert.That(stats.RowsScanned).IsEqualTo(2);
        await Assert.That(await follower.MetricAsync("terrabase_query_shapes_dropped_total", "reason=\"send_expired\""))
            .IsEqualTo(1);
    }

    [Test]
    public async Task Sender_caps_allow_existing_epochs_and_seven_day_retirement_rejects_stale_batches_without_removing_unknown_markers()
    {
        using var peers = new PeerTls();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(peers.Apply, time: time, managementCredential: true);
        var database = await api.CreateManagedDatabaseAsync();
        using (var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write"))
        {
            await SeedAsync(data, database);
            await QueryAsync(data, database, 1);
        }
        await api.Server.Services.GetRequiredService<QueryShapeRecorder>().FlushAsync(CancellationToken.None);
        using var owner = api.Service(api.User);
        var shape = (await ShapesAsync(owner, database)).Single();
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        const string limited = "node:replica";
        QueryShapeSenderRecord[] seeded = [.. Enumerable.Range(0, 15).Select(_ => new QueryShapeSenderRecord("alpha", limited,
            Convert.ToBase64String(EntityIds.New().ToByteArray()), 1, time.GetUtcNow()))];
        await SeedSendersAsync(catalog, seeded);
        using var channel = peers.CreateChannel(api.Server.PeerAddress!);
        var client = new CatalogQueryShapes.CatalogQueryShapesClient(channel);
        var old = Batch(database, shape, time, limited);
        await client.AddAsync(old);
        await Assert.That(await SenderCountAsync(catalog)).IsEqualTo(16);
        await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(2);
        var seventeenth = Batch(database, shape, time, limited);
        await RefusedAsync(client, seventeenth, StatusCode.ResourceExhausted);
        await Assert.That(await SenderBytesAsync(catalog, seventeenth)).IsNull();
        await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(2);

        var existing = Batch(database, shape, time, limited);
        existing.Epoch = ByteString.FromBase64(seeded[0].Epoch);
        existing.Sequence = 2;
        await client.AddAsync(existing);
        await client.AddAsync(existing);
        await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(3);

        // Only the original node's epochs become seven days old at the retirement boundary below.
        time.Advance(TimeSpan.FromSeconds(1));
        // A colon in a valid node name must not make its sixteen epochs count against a different node.
        await client.AddAsync(Batch(database, shape, time, "node"));
        await Assert.That(await SenderCountAsync(catalog)).IsEqualTo(17);
        await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(4);
        var unknown = new QueryShapeSenderRecord("future", "unsupported-node", Convert.ToBase64String(EntityIds.New().ToByteArray()),
            1, seeded[0].LastSeenAt);
        await SeedSendersAsync(catalog, Enumerable.Range(0, 1006).Select(index => new QueryShapeSenderRecord("alpha", "other-node-" + index,
            Convert.ToBase64String(EntityIds.New().ToByteArray()), 1, time.GetUtcNow())).Append(unknown));
        await Assert.That(await SenderCountAsync(catalog)).IsEqualTo(1024);
        var newcomer = Batch(database, shape, time, "new-node");
        await RefusedAsync(client, newcomer, StatusCode.ResourceExhausted);
        await Assert.That(await SenderBytesAsync(catalog, newcomer)).IsNull();
        await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(4);

        // Existing epochs can advance at both caps, and that activity postpones their retirement.
        existing = Batch(database, shape, time, limited, existing.Epoch, 3);
        await client.AddAsync(existing);
        await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(5);
        await Assert.That(await SenderCountAsync(catalog)).IsEqualTo(1024);
        var unsupported = Batch(database, shape, time, unknown.Sender);
        unsupported.Epoch = ByteString.FromBase64(unknown.Epoch);
        unsupported.Sequence = 2;
        var unknownBytes = (await SenderBytesAsync(catalog, unsupported))!;
        await RefusedAsync(client, unsupported, StatusCode.ResourceExhausted);
        await Assert.That((await SenderBytesAsync(catalog, unsupported))!.SequenceEqual(unknownBytes)).IsTrue();

        time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        seventeenth = Batch(database, shape, time, limited, seventeenth.Epoch);
        await RefusedAsync(client, seventeenth, StatusCode.ResourceExhausted);
        await Assert.That(await SenderCountAsync(catalog)).IsEqualTo(1024);
        await Assert.That(await SenderBytesAsync(catalog, old)).IsNotNull();

        time.Advance(TimeSpan.FromTicks(1));
        await client.AddAsync(seventeenth);
        // Fifteen inactive readable epochs retire; the recently active epoch and unknown format remain.
        await Assert.That(await SenderCountAsync(catalog)).IsEqualTo(1010);
        await Assert.That(await SenderBytesAsync(catalog, old)).IsNull();
        await Assert.That(await SenderBytesAsync(catalog, existing)).IsNotNull();
        await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(6);
        newcomer = Batch(database, shape, time, "new-node", newcomer.Epoch);
        await client.AddAsync(newcomer);
        await Assert.That(await SenderCountAsync(catalog)).IsEqualTo(1011);
        await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(7);

        await RefusedAsync(client, old, StatusCode.InvalidArgument);
        await Assert.That(await SenderBytesAsync(catalog, old)).IsNull();
        unsupported = Batch(database, shape, time, unknown.Sender, unsupported.Epoch, 2);
        await RefusedAsync(client, unsupported, StatusCode.ResourceExhausted);
        await Assert.That((await SenderBytesAsync(catalog, unsupported))!.SequenceEqual(unknownBytes)).IsTrue();
        await Assert.That(await SenderCountAsync(catalog)).IsEqualTo(1011);
        await Assert.That((await ShapesAsync(owner, database)).Single().Queries).IsEqualTo(7);
    }

    private static async Task SeedSendersAsync(SystemCatalog catalog, IEnumerable<QueryShapeSenderRecord> records)
    {
        // Setup goes through the real engine in bounded writes, leaving cap decisions to the real peer listener.
        foreach (var batch in records.Chunk(128))
        {
            await catalog.WriteAsync(async transaction =>
            {
                foreach (var record in batch)
                {
                    await SystemCatalog.PutAsync(transaction, "query_shape_senders", record.Sender + ":" + record.Epoch,
                        record, TerraBaseJsonContext.Default.QueryShapeSenderRecord, CancellationToken.None);
                }
                return true;
            }, static refused => throw new InvalidOperationException(refused.Message), CancellationToken.None);
        }
    }

    private static async Task<long> SenderCountAsync(SystemCatalog catalog) => await catalog.ReadAsync(
        transaction => transaction.CountDocumentsAsync("query_shape_senders", CancellationToken.None), static refused => throw new InvalidOperationException(refused.Message), CancellationToken.None);

    private static async Task<byte[]?> SenderBytesAsync(SystemCatalog catalog, QueryShapesBatch batch) => await catalog.ReadAsync(async transaction =>
        (await transaction.GetAsync("query_shape_senders", batch.Sender + ":" + batch.Epoch.ToBase64(), CancellationToken.None))?.Json.ToArray(), static refused => throw new InvalidOperationException(refused.Message),
        CancellationToken.None);

    private static async Task RefusedAsync(CatalogQueryShapes.CatalogQueryShapesClient client, QueryShapesBatch batch, StatusCode expected)
    {
        var error = await Assert.ThrowsAsync<RpcException>(async () => await client.AddAsync(batch));
        await Assert.That(error!.StatusCode).IsEqualTo(expected);
    }

    private static QueryShapesBatch Batch(ManagedDatabaseRef database, QueryShapeStats stats, TimeProvider time, string sender,
        ByteString? epoch = null, ulong sequence = 1) => new()
        {
            Format = "alpha",
            Sender = sender,
            Epoch = epoch ?? ByteString.CopyFrom(EntityIds.New().ToByteArray()),
            Sequence = sequence,
            Envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(JsonSerializer.SerializeToUtf8Bytes(
            new QueryShapeForwardPayload([new QueryShapeTransfer(database.Organization, database.Project, database.Id, stats)], time.GetUtcNow()),
            TerraBaseJsonContext.Default.QueryShapeForwardPayload)).Span),
        };

    private static async Task SeedAsync(HttpClient data, ManagedDatabaseRef database)
    {
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "one")),
            new { n = 1, email = new Faker().Internet.Email() });
        response.EnsureSuccessStatusCode();
    }

    private static async Task QueryAsync(HttpClient data, ManagedDatabaseRef database, int number)
    {
        using var response = await data.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/collections/people/query"), new
        {
            filter = new { and = new object[] { new { eq = new object[] { "/n", number } }, new { eq = EmailPredicate } } },
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<QueryShapeStats[]> ShapesAsync(HttpClient client, ManagedDatabaseRef database)
    {
        using var response = await client.GetAsync(ManagementApi.Uri(database.Path[3..] + "/query-shapes"));
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return [.. json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Deserialize(TerraBaseJsonContext.Default.QueryShapeStats)!)];
    }

    private sealed class AmbiguousShapeAcknowledgement(CatalogPrimaryClient inner) : ICatalogPrimaryClient
    {
        public ConcurrentQueue<QueryShapesBatch> Batches { get; } = new();
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AddUsageResult> AddUsageAsync(UsageBatch batch, CancellationToken cancellationToken) => inner.AddUsageAsync(batch, cancellationToken);

        public async Task<bool> AddQueryShapesAsync(QueryShapesBatch batch, CancellationToken cancellationToken)
        {
            Batches.Enqueue(batch.Clone());
            var accepted = await inner.AddQueryShapesAsync(batch, cancellationToken);
            if (Batches.Count == 1 && accepted)
            {
                Committed.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                throw new RpcException(new Status(StatusCode.Unavailable, "the query-shape acknowledgement was lost"));
            }
            return accepted;
        }
    }
}
