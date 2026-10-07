using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Real nodes replicating to each other over mutual TLS gRPC with LZ4 envelopes.</summary>
public sealed class ReplicationTests
{
    private static readonly Faker Faker = new();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Baselines_skip_oversized_live_and_deleted_keys_without_losing_progress(bool allInvalid)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-baseline-keys-").FullName;
        var skipped = new BaselineAdmissionLogs();
        // Three server starts and two replication rounds of 2 MiB keys: bound by the test's own timeout, not a shorter one.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        try
        {
            ReplicationTenant tenant;
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                clientTls.Apply(settings);
                settings["data-directory"] = data;
            }))
            {
                tenant = await ReplicationTenant.CreateAsync(seed, "legacy");
                using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
                using var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("legacy")}/collections", UriKind.Relative), new { name = "items" }, deadline.Token);
                collection.EnsureSuccessStatusCode();
            }

            // Restore oversized keys directly into a current database. SQLite generates them;
            // neither admission nor its continuation may materialize these 2 MiB keys in .NET.
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(DocumentApi.ConnectionString(Path.Combine(data,
                tenant.Database("legacy").OrganizationId, tenant.Database("legacy").ProjectId!, tenant.Database("legacy").DatabaseId + ".db"))))
            {
                await connection.OpenAsync(deadline.Token);
                using var restore = connection.CreateCommand();
                restore.CommandText = """
                    INSERT INTO documents VALUES (replace(hex(zeroblob(1048576)), '00', 'z'), 'live', 1, '{}');
                    INSERT INTO documents VALUES (replace(hex(zeroblob(1048576)), '00', 'z'), 'deleted', 1, NULL);
                    INSERT INTO documents VALUES ('items', replace(hex(zeroblob(1048576)), '00', 'z'), 1, '{}');
                    INSERT INTO documents VALUES ('items', replace(hex(zeroblob(1048576)), '00', 'z') || 'x', 1, NULL);
                    INSERT INTO documents SELECT 'items', 'after', 1, '{}' WHERE NOT $allInvalid;
                    UPDATE replication_state SET complete = 0 WHERE id = 1;
                    """;
                restore.Parameters.AddWithValue("$allInvalid", allInvalid);
                await restore.ExecuteNonQueryAsync(deadline.Token);
            }

            await using var receiver = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
            await tenant.BindAsync(receiver);
            await using var source = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["data-directory"] = data;
                settings["peer-addresses"] = receiver.PeerAddress!.ToString();
            }, services => services.AddSingleton<ILoggerProvider>(skipped));
            await TerraBaseTestServer.ReplicatedAsync(source);
            await Assert.That(skipped.Skipped).IsEqualTo(4);
            var store = (await source.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("legacy"), deadline.Token))!;
            await using (var read = await store.BeginTransactionAsync(write: false, deadline.Token))
            {
                // Group 1 created the collection; the baseline repeats its record beside the admissible documents.
                await Assert.That(await read.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(2);
                var changes = ChangeGroup.Decode(ChangeGroup.Encode((await read.ReadChangesAsync(2, deadline.Token))!));
                string[] admitted = allInvalid ? [] : ["after"];
                await Assert.That(changes.Where(change => change.Collection == "items").Select(change => change.Id)).IsEquivalentTo(admitted);
                await Assert.That(changes.Single(change => change.Collection == DocumentChange.Collections).Id).IsEqualTo("items");
            }

            using var client = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, "documents:write"));
            using var added = await client.PutAsJsonAsync(new Uri($"{tenant.Path("legacy")}/collections/items/documents/{ReplicationDocuments.Id("following")}", UriKind.Relative), new { value = "following" }, deadline.Token);
            added.EnsureSuccessStatusCode();
            await TerraBaseTestServer.ReplicatedAsync(source);
            var remote = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("legacy"), deadline.Token))!;
            await using var received = await remote.BeginTransactionAsync(write: false, deadline.Token);
            await Assert.That(await received.GetAsync("items", ReplicationDocuments.Id("following"), deadline.Token)).IsNotNull();
            await Assert.That((await received.GetAsync("items", "after", deadline.Token)) is not null).IsEqualTo(!allInvalid);
            await using var retained = new Microsoft.Data.Sqlite.SqliteConnection(DocumentApi.ConnectionString(tenant.File(source, "legacy")));
            await retained.OpenAsync(deadline.Token);
            using var count = retained.CreateCommand();
            count.CommandText = "SELECT count(*) FROM documents WHERE octet_length(collection) > 63 OR octet_length(id) > 1024;";
            await Assert.That((long)(await count.ExecuteScalarAsync(deadline.Token))!).IsEqualTo(4);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task A_missing_metric_sample_is_not_a_zero_replication_lag()
    {
        using var peerTls = new PeerTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(peerTls.Apply);
        await Assert.That(() => receiver.MetricAsync("terrabase_replication_sender_durable_lag")).Throws<InvalidOperationException>();
        await Assert.That(() => TerraBaseTestServer.ReplicatedAsync(receiver)).Throws<InvalidOperationException>();

        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        });

        // Host startup must publish both samples before the background journal enumeration completes.
        await Assert.That(await sender.MetricAsync("terrabase_replication_sender_initialized")).IsBetween(0, 1);
        await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag")).IsEqualTo(0);
        await TerraBaseTestServer.ReplicatedAsync(sender);
        await Assert.That(await sender.MetricAsync("terrabase_replication_sender_initialized")).IsEqualTo(1);
        await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag")).IsEqualTo(0);
        await Assert.That(() => sender.MetricAsync("terrabase_replication_sender_durable_lag", "result=\"missing\""))
            .Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.Forbidden)]
    [Arguments(HttpStatusCode.InternalServerError)]
    public async Task Listing_documents_tolerates_only_a_database_not_found(HttpStatusCode status)
    {
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(clientTls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(server, status == HttpStatusCode.InternalServerError ? ["shop"] : []);
        var key = status == HttpStatusCode.Unauthorized ? null
            : await tenant.KeyAsync(server, status == HttpStatusCode.Forbidden ? "documents:write" : "documents:read");
        using var client = clientTls.CreateClient(server.ClientAddress!, key);
        if (status == HttpStatusCode.InternalServerError)
        {
            await server.Services.GetRequiredService<IStorageEngine>().DeleteDatabaseAsync(tenant.Database("shop"), CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(tenant.File(server, "shop"))!);
            await File.WriteAllBytesAsync(tenant.File(server, "shop"), Faker.Random.Bytes(4096));
        }

        if (status == HttpStatusCode.NotFound)
        {
            await Assert.That(await ListAsync(tenant, client, "shop", "people")).IsEmpty();
        }
        else
        {
            var error = await Assert.That(async () => { await ListAsync(tenant, client, "shop", "people"); }).Throws<HttpRequestException>();
            await Assert.That(error!.StatusCode).IsEqualTo(status);
        }
    }

    [Test]
    public async Task Listing_documents_does_not_hide_a_missing_collection()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(server, "shop");
        using var reader = tls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:read"));
        var error = await Assert.That(async () => await ListAsync(tenant, reader, "shop", "missing")).Throws<HttpRequestException>();
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Two_nodes_that_write_at_the_same_time_converge()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var (a, b) = await TerraBaseTestServer.StartPairAsync((settings, port, peerPort) => Configure(settings, peerTls, clientTls, port, peerPort));
        await using var disposeA = a;
        await using var disposeB = b;
        await Assert.That(a.PeerAddress!.Port).IsNotEqualTo(b.PeerAddress!.Port);
        var tenant = await ReplicationTenant.CreateAsync(a, "shop");
        await tenant.BindAsync(b);
        using var clientA = clientTls.CreateClient(a.ClientAddress!, await tenant.KeyAsync(a, "documents:read", "documents:write"));
        using var clientB = clientTls.CreateClient(b.ClientAddress!, await tenant.KeyAsync(b, "documents:read", "documents:write"));
        // The collection created on a reaches b.
        await CreateCollectionAsync(tenant, clientA, "shop", "people");
        await TerraBaseTestServer.ReplicatedAsync(a, b);

        // Both nodes write their own documents and the same "shared" document at once.
        await Task.WhenAll(PutAsync(tenant, clientA, "a1"), PutAsync(tenant, clientA, "a2"), PutAsync(tenant, clientA, "shared"), PutAsync(tenant, clientB, "b1"), PutAsync(tenant, clientB, "shared"));
        // Barrier: every group either node committed is durably applied on the other.
        await TerraBaseTestServer.ReplicatedAsync(a, b);
        var firstA = await ListAsync(tenant, clientA, "shop", "people");
        var firstB = await ListAsync(tenant, clientB, "shop", "people");
        var original = await VersionAsync(tenant, a, "shop", "people", ReplicationDocuments.Id("a1"));
        foreach (var (id, _) in firstA)
        {
            await Assert.That(await VersionAsync(tenant, b, "shop", "people", id))
                .IsEqualTo(await VersionAsync(tenant, a, "shop", "people", id));
        }

        // Each node then changes a document that came from the other one.
        await PutAsync(tenant, clientB, "a1");
        await DeleteAsync(tenant, clientA, "b1");
        await TerraBaseTestServer.ReplicatedAsync(a, b);
        var secondA = await ListAsync(tenant, clientA, "shop", "people");
        var secondB = await ListAsync(tenant, clientB, "shop", "people");
        foreach (var (id, _) in firstA)
        {
            await Assert.That(await VersionAsync(tenant, b, "shop", "people", id))
                .IsEqualTo(await VersionAsync(tenant, a, "shop", "people", id));
        }

        string[] initialIds = ["a1", "a2", "b1", "shared"];
        string[] remainingIds = ["a1", "a2", "shared"];
        await Assert.That(firstB).IsEquivalentTo(firstA);
        await Assert.That(firstA.Select(document => document.Id)).IsEquivalentTo(initialIds.Select(ReplicationDocuments.Id));
        await Assert.That(secondB).IsEquivalentTo(secondA);
        await Assert.That(secondA.Select(document => document.Id)).IsEquivalentTo(remainingIds.Select(ReplicationDocuments.Id));
        var changed = await VersionAsync(tenant, a, "shop", "people", ReplicationDocuments.Id("a1"));
        await Assert.That(changed.Version).IsGreaterThan(original.Version);
        await Assert.That(await VersionAsync(tenant, b, "shop", "people", ReplicationDocuments.Id("a1"))).IsEqualTo(changed);
        await Assert.That(secondA.Single(document => document.Id == ReplicationDocuments.Id("shared")).Data)
            .IsEqualTo(firstA.Single(document => document.Id == ReplicationDocuments.Id("shared")).Data);
    }

    [Test]
    public async Task A_version_read_on_one_member_deletes_the_document_on_the_other()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var (a, b) = await TerraBaseTestServer.StartPairAsync((settings, port, peerPort) => Configure(settings, peerTls, clientTls, port, peerPort));
        await using var disposeA = a;
        await using var disposeB = b;
        var tenant = await ReplicationTenant.CreateAsync(a, "shop");
        await tenant.BindAsync(b);
        using var clientA = clientTls.CreateClient(a.ClientAddress!, await tenant.KeyAsync(a, "documents:read", "documents:write"));
        using var clientB = clientTls.CreateClient(b.ClientAddress!, await tenant.KeyAsync(b, "documents:read", "documents:write"));
        await CreateCollectionAsync(tenant, clientA, "shop", "people");
        await PutAsync(tenant, clientA, "probe");
        await TerraBaseTestServer.ReplicatedAsync(a, b);

        // ts-auth's readiness probe behind a round-robin balancer: read on one member, delete conditionally on the other.
        var document = new Uri($"{tenant.Path("shop")}/collections/people/documents/{ReplicationDocuments.Id("probe")}", UriKind.Relative);
        using var readA = await clientA.GetAsync(document);
        using var readB = await clientB.GetAsync(document);
        readA.EnsureSuccessStatusCode();
        readB.EnsureSuccessStatusCode();
        await Assert.That(readB.Headers.ETag).IsEqualTo(readA.Headers.ETag);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, document);
        delete.Headers.IfMatch.Add(readA.Headers.ETag!);
        using var deleted = await clientB.SendAsync(delete);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await TerraBaseTestServer.ReplicatedAsync(a, b);
        using var tombstoneA = await clientA.GetAsync(document);
        using var tombstoneB = await clientB.GetAsync(document);
        await Assert.That(tombstoneA.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(tombstoneA.Headers.ETag).IsNotNull();
        await Assert.That(tombstoneB.Headers.ETag).IsEqualTo(tombstoneA.Headers.ETag);
    }

    [Test]
    public async Task Collections_and_indexes_created_and_dropped_on_one_member_follow_on_the_other()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        var (a, b) = await TerraBaseTestServer.StartPairAsync((settings, port, peerPort) => Configure(settings, peerTls, clientTls, port, peerPort));
        await using var disposeA = a;
        await using var disposeB = b;
        var tenant = await ReplicationTenant.CreateAsync(a, "shop");
        await tenant.BindAsync(b);
        using var clientA = clientTls.CreateClient(a.ClientAddress!, await tenant.KeyAsync(a, "documents:read", "documents:write"));
        using var clientB = clientTls.CreateClient(b.ClientAddress!, await tenant.KeyAsync(b, "documents:read", "documents:write"));
        var collection = Faker.Random.String2(12, "abcdefghijklmnopqrstuvwxyz");
        var path = new Uri($"{tenant.Path("shop")}/collections/{collection}", UriKind.Relative);
        // Empty: nothing but the creation itself can bring it to b.
        await CreateCollectionAsync(tenant, clientA, "shop", collection);
        await Assert.That(DocumentField.TryParse("address.city", out var city)).IsTrue();
        var index = new IndexDefinition($"{collection}_by_city", collection, [city, DocumentField.Id]);
        await WriteAsync(a, transaction => transaction.CreateIndexAsync(index, token));
        await TerraBaseTestServer.ReplicatedAsync(a, b);

        using (var created = await clientA.GetAsync(path, token))
        using (var replicated = await clientB.GetAsync(path, token))
        {
            replicated.EnsureSuccessStatusCode();
            await Assert.That(await replicated.Content.ReadAsStringAsync(token)).IsEqualTo(await created.Content.ReadAsStringAsync(token));
        }

        var replicatedIndex = (await IndexesAsync(b)).Single();
        await Assert.That(replicatedIndex.Name).IsEqualTo(index.Name);
        await Assert.That(replicatedIndex.Fields).IsEquivalentTo(index.Fields);
        using (var dropped = await clientA.DeleteAsync(path, token))
        {
            dropped.EnsureSuccessStatusCode();
        }

        await WriteAsync(a, transaction => transaction.DropIndexAsync(index.Name, token));
        await TerraBaseTestServer.ReplicatedAsync(a, b);
        using (var missing = await clientB.GetAsync(path, token))
        {
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await Assert.That(await IndexesAsync(b)).IsEmpty();

        async Task WriteAsync(TerraBaseTestServer node, Func<IStorageTransaction, ValueTask<bool>> write)
        {
            var store = await node.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("shop"), token);
            await using var transaction = await store!.BeginTransactionAsync(write: true, token);
            await Assert.That(await write(transaction)).IsTrue();
            await transaction.CommitAsync(token);
        }

        async Task<List<IndexDefinition>> IndexesAsync(TerraBaseTestServer node)
        {
            var store = await node.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("shop"), token);
            await using var transaction = await store!.BeginTransactionAsync(write: false, token);
            return [.. (await transaction.ListIndexesAsync(token)).Where(found => found.Collection == collection)];
        }
    }

    [Test]
    public async Task A_restarted_node_resumes_after_its_peers_applied_prefix()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-sender-").FullName;
        try
        {
            await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
            });
            using var reader = clientTls.CreateClient(receiver.ClientAddress!, apiKey: null);
            ReplicationTenant tenant;
            void Sender(Dictionary<string, string> settings)
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["data-directory"] = data;
                settings["peer-addresses"] = receiver.PeerAddress!.ToString();
            }

            double plain, encoded;
            var originalVersions = new List<DocumentVersion>();
            await using (var sender = await TerraBaseTestServer.StartAsync(Sender))
            {
                tenant = await ReplicationTenant.CreateAsync(sender, "orders");
                await tenant.BindAsync(receiver);
                reader.DefaultRequestHeaders.Authorization = new("Bearer", await tenant.KeyAsync(receiver, "documents:read"));
                using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
                await CreateCollectionAsync(tenant, writer, "orders", "orders");
                foreach (var id in new[] { "o1", "o2", "o3" })
                {
                    await PutOrderAsync(tenant, writer, id);
                    originalVersions.Add(await VersionAsync(tenant, sender, "orders", "orders", ReplicationDocuments.Id(id)));
                }

                await TerraBaseTestServer.ReplicatedAsync(sender);
                await Assert.That((await ListAsync(tenant, reader, "orders")).Count).IsEqualTo(3);
                plain = await sender.MetricAsync("terrabase_replication_sent_plain_bytes_total");
                encoded = await sender.MetricAsync("terrabase_replication_sent_encoded_bytes_total");
            }

            await using var restarted = await TerraBaseTestServer.StartAsync(Sender);
            using (var writer = clientTls.CreateClient(restarted.ClientAddress!, await tenant.KeyAsync(restarted, "documents:write")))
            {
                await PutOrderAsync(tenant, writer, "o4");
            }

            await TerraBaseTestServer.ReplicatedAsync(restarted);
            var orders = await ListAsync(tenant, reader, "orders");
            await Assert.That(orders.Count).IsEqualTo(4);

            // The restarted node sent only the group its peer had not applied.
            await Assert.That(await restarted.MetricAsync("terrabase_replication_sent_groups_total")).IsEqualTo(1);
            string[] orderIds = ["o1", "o2", "o3", "o4"];
            foreach (var (id, index) in orderIds.Select((id, index) => (id, index)))
            {
                var version = await VersionAsync(tenant, restarted, "orders", "orders", ReplicationDocuments.Id(id));
                await Assert.That(await VersionAsync(tenant, receiver, "orders", "orders", ReplicationDocuments.Id(id))).IsEqualTo(version);
                if (index < originalVersions.Count)
                {
                    await Assert.That(version).IsEqualTo(originalVersions[index]);
                }
                else
                {
                    await Assert.That(version.Version).IsGreaterThan(originalVersions.Max(original => original.Version));
                }
            }

            await Assert.That(encoded).IsLessThan(plain);
            TestContext.Current?.OutputWriter.WriteLine($"3 orders: {plain} change group bytes, {encoded} envelope bytes after LZ4");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task A_database_the_peer_cannot_open_does_not_hold_back_the_others()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        });
        var tenant = await ReplicationTenant.CreateAsync(sender, "broken", "healthy");
        await tenant.BindAsync(receiver);
        // Retire the cached empty store before corrupting its file; the catalog identity remains registered.
        await receiver.Services.GetRequiredService<IStorageEngine>().DeleteDatabaseAsync(tenant.Database("broken"), CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(tenant.File(receiver, "broken"))!);
        await File.WriteAllBytesAsync(tenant.File(receiver, "broken"), Faker.Random.Bytes(4096));
        using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));

        foreach (var database in new[] { "broken", "healthy" })
        {
            await CreateCollectionAsync(tenant, writer, database, database);
            using var response = await writer.PutAsJsonAsync(new Uri($"{tenant.Path(database)}/collections/{database}/documents/{ReplicationDocuments.Id("d1")}", UriKind.Relative), new { note = Faker.Lorem.Sentence() });
            response.EnsureSuccessStatusCode();
        }

        // "broken" keeps aggregate lag positive. Wait for this database's durable frontier before
        // listing, so an as-yet-uncreated collection never becomes a tolerated listing error.
        var source = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("healthy"), CancellationToken.None))!;
        ReadOnlyMemory<byte> origin;
        long head;
        await using (var read = await source.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            origin = await read.GetOriginAsync(CancellationToken.None);
            head = await read.GetLastChangeSequenceAsync(CancellationToken.None);
        }

        var target = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("healthy"), CancellationToken.None))!;
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            await using var read = await target.BeginTransactionAsync(write: false, token);
            return await read.GetAppliedSequenceAsync(origin, token) == head;
        });
        await Assert.That((await ListAsync(tenant, reader, "healthy", "healthy")).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Writes_made_while_replication_was_off_reach_the_peer_once_it_is_on()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-toggle-").FullName;
        try
        {
            await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
            });
            using var reader = clientTls.CreateClient(receiver.ClientAddress!, apiKey: null);
            ReplicationTenant? tenant = null;
            async Task RunAsync(bool replicate, Func<HttpClient, Task> work)
            {
                await using var node = await TerraBaseTestServer.StartAsync(settings =>
                {
                    peerTls.Apply(settings);
                    clientTls.Apply(settings);
                    settings["data-directory"] = data;
                    if (replicate)
                    {
                        settings["peer-addresses"] = receiver.PeerAddress!.ToString();
                    }
                });
                if (tenant is null)
                {
                    tenant = await ReplicationTenant.CreateAsync(node, "shop");
                    await tenant.BindAsync(receiver);
                    reader.DefaultRequestHeaders.Authorization = new("Bearer", await tenant.KeyAsync(receiver, "documents:read"));
                }

                using var writer = clientTls.CreateClient(node.ClientAddress!, await tenant.KeyAsync(node, "documents:write"));
                if (replicate)
                {
                    await TerraBaseTestServer.ReplicatedAsync(node);
                }
                await work(writer);
            }

            await RunAsync(replicate: false, async writer =>
            {
                await CreateCollectionAsync(tenant!, writer, "shop", "people");
                await PutAsync(tenant!, writer, "kept");
                await PutAsync(tenant!, writer, "deleted");
            });
            // Turned on, the node sends what it wrote while off as a baseline.
            await RunAsync(replicate: true, _ => TerraBaseTestServer.EventuallyAsync(async token => (await ListAsync(tenant!, reader, "shop", "people", token)).Count == 2));
            await RunAsync(replicate: false, writer => DeleteAsync(tenant!, writer, "deleted"));
            // A delete made while off is not lost when replication resumes.
            await RunAsync(replicate: true, _ => TerraBaseTestServer.EventuallyAsync(async token => (await ListAsync(tenant!, reader, "shop", "people", token)).Count == 1));

            await Assert.That((await ListAsync(tenant!, reader, "shop", "people")).Single().Id).IsEqualTo(ReplicationDocuments.Id("kept"));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task A_branch_reaches_the_peer_with_the_content_it_was_copied_with()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var credential = Directory.CreateTempSubdirectory("terrabase-branch-").FullName;
        try
        {
            const string Secret = "replication-test-management-credential-01";
            await File.WriteAllTextAsync(Path.Combine(credential, "credential"), Secret);
            await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
            });
            await using var sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["management-credential-file"] = Path.Combine(credential, "credential");
                settings["peer-addresses"] = receiver.PeerAddress!.ToString();
            });
            var tenant = await ReplicationTenant.CreateAsync(sender, "main");
            // The service credential is accepted on the loopback administrative listener only.
            using var dashboard = new HttpClient { BaseAddress = sender.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
            dashboard.DefaultRequestHeaders.Authorization = new("Bearer", Secret);
            dashboard.DefaultRequestHeaders.Add("X-TerraBase-User-Id", tenant.UserId);
            using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));

            var organization = tenant.OrganizationId;
            var databases = $"/v1/management/organizations/{organization}/projects/{tenant.ProjectId}/databases";
            var main = tenant.Database("main").DatabaseId!;
            await Assert.That(await CreateAsync(dashboard, organization, $"{databases}/{main}", null, "id")).IsEqualTo(main);
            await CreateCollectionAsync(tenant, writer, "main", "people");
            var id = ReplicationDocuments.Id("p1");
            var person = new { name = Faker.Name.FullName() + " O'Neil" };
            var body = JsonSerializer.Serialize(person);
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var inserted = await writer.PutAsync(new Uri($"{tenant.Path("main")}/collections/people/documents/{id}", UriKind.Relative), content);
            inserted.EnsureSuccessStatusCode();
            var insertedVersion = await VersionAsync(tenant, sender, "main", "people", id);
            var branch = await CreateAsync(dashboard, organization, $"{databases}/{main}/branches", "preview", "id");
            await tenant.CaptureDatabaseAsync(sender, "preview");
            await Assert.That(tenant.Database("preview").DatabaseId).IsEqualTo(branch);
            using var copiedReader = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:read"));
            var copiedSnapshot = await ListAsync(tenant, copiedReader, "preview", "people");
            var copiedVersion = await VersionAsync(tenant, sender, "preview", "people", id);
            await Assert.That(copiedSnapshot.Select(document => document.Id)).IsEquivalentTo([id]);
            using var expected = JsonDocument.Parse(body);
            using var listed = JsonDocument.Parse(copiedSnapshot.Single().Data);
            // HTTP JSON serialization can rewrite escapes; verify values through HTTPS and the
            // original bytes through the running store, before comparing both nodes' listings.
            await Assert.That(JsonElement.DeepEquals(listed.RootElement, expected.RootElement)).IsTrue();
            var copiedStore = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("preview"), CancellationToken.None))!;
            await using (var read = await copiedStore.BeginTransactionAsync(write: false, CancellationToken.None))
            {
                await Assert.That(System.Text.Encoding.UTF8.GetString((await read.GetAsync("people", id, CancellationToken.None))!.Json.Span)).IsEqualTo(body);
            }
            await Assert.That(copiedVersion).IsEqualTo(insertedVersion);
            await tenant.BindAsync(receiver);
            using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));

            await TerraBaseTestServer.ReplicatedAsync(sender);
            await Assert.That(await ListAsync(tenant, reader, "preview", "people")).IsEquivalentTo(copiedSnapshot);
            await Assert.That(await VersionAsync(tenant, receiver, "preview", "people", id)).IsEqualTo(copiedVersion);
            var remoteStore = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("preview"), CancellationToken.None))!;
            await using var remote = await remoteStore.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That(System.Text.Encoding.UTF8.GetString((await remote.GetAsync("people", id, CancellationToken.None))!.Json.Span)).IsEqualTo(body);
        }
        finally
        {
            Directory.Delete(credential, recursive: true);
        }
    }

    [Test]
    [Arguments("prefix")]
    [Arguments("digest")]
    [Arguments("peer")]
    [Arguments("epoch")]
    [Arguments("origin")]
    [Arguments("replica")]
    [Arguments("shard")]
    [Arguments("tenant")]
    [Arguments("project")]
    [Arguments("database")]
    public async Task An_acknowledgment_that_does_not_match_the_stream_releases_nothing(string mismatch)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var deliveries = new System.Collections.Concurrent.ConcurrentQueue<ulong>();
        var malformedAttempts = 0;
        var firstAttemptEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            if (Volatile.Read(ref malformedAttempts) > 0)
            {
                // Hold the next Welcome so reconciliation cannot reset progress before we inspect it.
                await resume.Task.WaitAsync(context.CancellationToken);
            }

            await responses.WriteAsync(FakePeer.Welcome(requests.Current.Hello, out var member));
            var sentMalformed = false;
            try
            {
                while (await requests.MoveNext(context.CancellationToken))
                {
                    if (requests.Current.CommittedGroup is { } batch)
                    {
                        deliveries.Enqueue(batch.EventId.Sequence);
                        var ack = FakePeer.Ack(member, batch, batch.EventId.Sequence);
                        switch (mismatch)
                        {
                            case "digest":
                                ack.Durable.JournalDigest = Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(JournalDigest.Length));
                                break;
                            case "prefix":
                                ack.Durable.DurablePrefix = 1000;
                                break;
                            case "peer":
                                var other = member.Clone();
                                other.MemberId = other.MemberIncarnation = Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(16));
                                ack.Durable.Context.AcknowledgingPeer = other;
                                break;
                            case "epoch":
                                ack.Durable.Context.MembershipEpoch = 7;
                                break;
                            case "replica":
                                ack.Durable.Context.EventId.Origin.ReplicaId = Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(16));
                                break;
                            case "tenant":
                                ack.Durable.Context.EventId.TenantId = Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(16));
                                break;
                            case "project":
                                ack.Durable.Context.EventId.ProjectId = Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(16));
                                break;
                            case "database":
                                ack.Durable.Context.EventId.DatabaseId = Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(16));
                                break;
                            case "shard":
                                ack.Durable.Context.EventId.ShardId = 1;
                                break;
                            default:
                                ack.Durable.Context.EventId.Origin.Incarnation = Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(16));
                                break;
                        }

                        await responses.WriteAsync(ack);
                        sentMalformed = true;
                        Interlocked.Increment(ref malformedAttempts);
                    }
                }
            }
            finally
            {
                if (sentMalformed)
                {
                    firstAttemptEnded.TrySetResult();
                }
            }
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
        });
        var tenant = await ReplicationTenant.CreateAsync(sender, "shop");
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        await CreateCollectionAsync(tenant, writer, "shop", "people");

        await PutAsync(tenant, writer, "only");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await firstAttemptEnded.Task.WaitAsync(deadline.Token);
        try
        {
            // An accepted malformed ACK would release a group (the collection's creation or the put); periodic replay alone
            // cannot prove rejection.
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag", "", deadline.Token)).IsEqualTo(2);
        }
        finally
        {
            resume.TrySetResult();
        }

        // The sender rejects the acknowledgment, keeps the group and sends it again on a new stream.
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(deliveries.Count(sequence => sequence == 1) >= 2));
    }

    [Test]
    [Arguments("message")]
    [Arguments("credit")]
    [Arguments("group")]
    [Arguments("format")]
    [Arguments("record")]
    [Arguments("merge")]
    [Arguments("minor")]
    [Arguments("raised")]
    [Arguments("frontier")]
    [Arguments("ahead")]
    [Arguments("tenant")]
    [Arguments("project")]
    [Arguments("database")]
    public async Task A_sender_sends_nothing_its_peers_Welcome_does_not_allow(string welcome)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var completed = 0;
        var batches = 0;
        await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            var hello = requests.Current.Hello;
            var limits = hello.Limits.Clone();
            switch (welcome)
            {
                case "message":
                    limits.MaxGrpcMessageBytes = 256;
                    break;
                case "credit":
                    limits.ByteCredit = 64;
                    break;
                case "group":
                    // Below the collection's creation (45 bytes) and the document.
                    limits.MaxTransactionGroupBytes = 32;
                    break;
                case "raised":
                    // Above what the sender offered: a Welcome may only lower a limit.
                    limits.ByteCredit = hello.Limits.ByteCredit + 1;
                    break;
                default:
                    break;
            }

            var answer = FakePeer.Welcome(hello, out _, limits);
            switch (welcome)
            {
                case "format":
                    answer.Welcome.EventFormatVersion = 2;
                    break;
                case "record":
                    answer.Welcome.RecordFormatVersion = 2;
                    break;
                case "merge":
                    answer.Welcome.MergePolicyVersion = 2;
                    break;
                case "tenant":
                    answer.Welcome.LocalIdentity.TenantId = Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(16));
                    break;
                case "project":
                    answer.Welcome.LocalIdentity.ProjectId = Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(16));
                    break;
                case "database":
                    answer.Welcome.LocalIdentity.DatabaseId = Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(16));
                    break;
                case "minor":
                    answer.Welcome.ProtocolVersion.Minor = 1;
                    break;
                case "frontier":
                case "ahead":
                    answer.Welcome.Frontiers.Add(new PeerFrontier
                    {
                        Origin = new OriginIdentity
                        {
                            Incarnation = hello.LocalIdentity.MemberIncarnation,
                            ReplicaId = welcome == "ahead" ? hello.LocalIdentity.MemberId : Google.Protobuf.ByteString.CopyFrom(Faker.Random.Bytes(16)),
                        },
                        DurableSequence = welcome == "ahead" ? 1000UL : 1UL,
                    });
                    break;
                default:
                    break;
            }

            await responses.WriteAsync(answer);
            try
            {
                while (await requests.MoveNext(context.CancellationToken))
                {
                    if (requests.Current.CommittedGroup is not null)
                    {
                        Interlocked.Increment(ref batches);
                    }
                }
            }
            finally
            {
                Interlocked.Increment(ref completed);
            }
        });
        var data = Directory.CreateTempSubdirectory("terrabase-welcome-").FullName;
        try
        {
            // Commit over the client listener before starting any sender, so every turn has this group.
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                clientTls.Apply(settings);
                settings["data-directory"] = data;
            }))
            {
                var tenant = await ReplicationTenant.CreateAsync(seed, "limits");
                using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
                await CreateCollectionAsync(tenant, writer, "limits", "blobs");
                using var response = await writer.PutAsJsonAsync(
                    new Uri($"{tenant.Path("limits")}/collections/blobs/documents/{ReplicationDocuments.Id("b")}", UriKind.Relative),
                    new { data = Convert.ToBase64String(Faker.Random.Bytes(1024)) });
                response.EnsureSuccessStatusCode();
            }

            await using var sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                settings["data-directory"] = data;
                settings["peer-addresses"] = peer.Address.ToString();
            });

            // Require a finished attempt against the committed group; a new Hello alone does not count.
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Volatile.Read(ref completed) >= 1));
            await Assert.That(Volatile.Read(ref batches)).IsEqualTo(0);
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag")).IsGreaterThan(0);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task Exhausted_byte_credit_blocks_a_prepared_group_until_a_durable_ACK_releases_it()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        const ulong credit = 6 * 1024;
        var data = Directory.CreateTempSubdirectory("terrabase-credit-").FullName;
        var allowWelcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<CommittedGroupBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<CommittedGroupBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ReplicationTenant tenant;
            // All groups exist before the sender captures its head, so a zero-head discovery cannot satisfy the test.
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                clientTls.Apply(settings);
                settings["data-directory"] = data;
            }))
            {
                tenant = await ReplicationTenant.CreateAsync(seed, "credit");
                using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
                await CreateCollectionAsync(tenant, writer, "credit", "blobs");
                foreach (var id in Enumerable.Range(0, 4))
                {
                    using var response = await writer.PutAsJsonAsync(
                        new Uri($"{tenant.Path("credit")}/collections/blobs/documents/{ReplicationDocuments.Id($"b{id}")}", UriKind.Relative),
                        new { data = Convert.ToBase64String(Faker.Random.Bytes(3 * 1024)) });
                    response.EnsureSuccessStatusCode();
                }
            }

            ulong applied = 0;
            ReadOnlyMemory<byte> appliedProof = default;
            await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
            {
                await Assert.That(await requests.MoveNext(context.CancellationToken)).IsTrue();
                var hello = requests.Current.Hello;
                var limits = hello.Limits.Clone();
                limits.ByteCredit = credit;
                await allowWelcome.Task.WaitAsync(context.CancellationToken);
                var welcome = FakePeer.Welcome(hello, out var member, limits);
                welcome.Welcome.Frontiers.Add(new PeerFrontier
                {
                    Origin = new OriginIdentity { ReplicaId = hello.LocalIdentity.MemberIncarnation, Incarnation = hello.LocalIdentity.MemberIncarnation },
                    DurableSequence = applied,
                    JournalDigest = Google.Protobuf.ByteString.CopyFrom(applied == 0 ? JournalDigest.Initial(hello.LocalIdentity.MemberIncarnation.Span) : appliedProof.Span),
                });
                await responses.WriteAsync(welcome, context.CancellationToken);
                while (await requests.MoveNext(context.CancellationToken))
                {
                    // Group 1 creates the collection; group 2 is the first document.
                    var batch = requests.Current.CommittedGroup;
                    if (batch.EventId.Sequence == 2)
                    {
                        first.TrySetResult(batch);
                        await releaseAck.Task.WaitAsync(context.CancellationToken);
                    }
                    else if (batch.EventId.Sequence > 2)
                    {
                        second.TrySetResult(batch);
                    }

                    applied = batch.EventId.Sequence;
                    appliedProof = batch.JournalDigest.Memory;
                    await responses.WriteAsync(FakePeer.Ack(member, batch, applied), context.CancellationToken);
                }
            });
            await using var sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                settings["data-directory"] = data;
                settings["peer-addresses"] = peer.Address.ToString();
            });
            var blocked = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            long sent = 0;
            var scope = sender.Services.GetRequiredService<IMeterFactory>();
            using var observer = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Scope == scope && instrument.Name is "terrabase.replication.sent.groups" or "terrabase.replication.sender.credit.waits")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            observer.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (instrument.Name == "terrabase.replication.sent.groups")
                {
                    if (Interlocked.Add(ref sent, value) > 2 && !releaseAck.Task.IsCompleted)
                    {
                        blocked.TrySetException(new InvalidDataException("the sender exceeded byte credit before the held ACK was released"));
                    }
                }
                else if (value > 0)
                {
                    blocked.TrySetResult(Interlocked.Read(ref sent));
                }
            });
            observer.Start();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            allowWelcome.SetResult();
            // Positive evidence: group 3 is prepared, but the actual sender has reached its byte-credit wait.
            await Assert.That(await blocked.Task.WaitAsync(deadline.Token)).IsEqualTo(2);
            var held = await first.Task.WaitAsync(deadline.Token);
            await Assert.That(held.EventId.Sequence).IsEqualTo(2UL);
            await Assert.That((ulong)held.EncodedApplicationBytes).IsLessThanOrEqualTo(credit);
            await Assert.That(second.Task.IsCompleted).IsFalse();
            // Only group 1's ACK went out.
            await TerraBaseTestServer.EventuallyAsync(async token => await sender.MetricAsync("terrabase_replication_sender_durable_lag", "", token) == 4);
            releaseAck.SetResult();
            var released = await second.Task.WaitAsync(deadline.Token);
            await Assert.That(released.EventId.Sequence).IsEqualTo(3UL);
            await Assert.That((ulong)held.EncodedApplicationBytes + released.EncodedApplicationBytes).IsGreaterThan(credit);
            await Assert.That((ulong)released.EncodedApplicationBytes).IsLessThanOrEqualTo(credit);
            await TerraBaseTestServer.ReplicatedAsync(sender);
            await Assert.That(Interlocked.Read(ref sent)).IsEqualTo(5);
        }
        finally
        {
            allowWelcome.TrySetResult();
            releaseAck.TrySetResult();
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task The_sender_stays_within_the_byte_credit_the_peer_grants()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        const ulong credit = 6 * 1024;
        ulong mostUnacknowledgedBytes = 0;
        var delivered = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waits = Channel.CreateBounded<bool>(1);
        var data = Directory.CreateTempSubdirectory("terrabase-credit-accounting-").FullName;
        try
        {
            ReplicationTenant tenant;
            // A fixed, complete journal head makes each of the first three ACKs a genuine credit release.
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                clientTls.Apply(settings);
                settings["data-directory"] = data;
            }))
            {
                tenant = await ReplicationTenant.CreateAsync(seed, "credit");
                using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
                await CreateCollectionAsync(tenant, writer, "credit", "blobs");
                foreach (var id in Enumerable.Range(0, 4))
                {
                    using var response = await writer.PutAsJsonAsync(
                        new Uri($"{tenant.Path("credit")}/collections/blobs/documents/{ReplicationDocuments.Id($"b{id}")}", UriKind.Relative),
                        new { data = Convert.ToBase64String(Faker.Random.Bytes(3 * 1024)) });
                    response.EnsureSuccessStatusCode();
                }
            }

            await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
            {
                await requests.MoveNext(context.CancellationToken);
                var hello = requests.Current.Hello;
                var limits = hello.Limits.Clone();
                limits.ByteCredit = credit;
                await ready.Task.WaitAsync(context.CancellationToken);
                await responses.WriteAsync(FakePeer.Welcome(hello, out var member, limits));
                var reading = requests.MoveNext(context.CancellationToken);
                while (await reading)
                {
                    var batch = requests.Current.CommittedGroup;
                    ulong outstanding = batch.EncodedApplicationBytes;
                    var count = 1;
                    reading = requests.MoveNext(context.CancellationToken);
                    // Five groups: the collection's creation and four documents.
                    if (Volatile.Read(ref delivered) + count < 5)
                    {
                        var blocked = waits.Reader.ReadAsync(context.CancellationToken).AsTask();
                        // Count any queued groups while withholding ACK, until the sender positively reports
                        // that its next prepared group cannot fit in the remaining byte credit.
                        while (await Task.WhenAny(reading, blocked) == reading)
                        {
                            await Assert.That(await reading).IsTrue();
                            batch = requests.Current.CommittedGroup;
                            outstanding += batch.EncodedApplicationBytes;
                            count++;
                            reading = requests.MoveNext(context.CancellationToken);
                        }

                        await blocked;
                    }

                    mostUnacknowledgedBytes = Math.Max(mostUnacknowledgedBytes, outstanding);
                    await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence));
                    Interlocked.Add(ref delivered, count);
                }
            });
            await using var sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                settings["data-directory"] = data;
                settings["peer-addresses"] = peer.Address.ToString();
            });
            var scope = sender.Services.GetRequiredService<IMeterFactory>();
            using var observer = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Scope == scope && instrument.Name == "terrabase.replication.sender.credit.waits")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            observer.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                if (value > 0)
                {
                    waits.Writer.TryWrite(true);
                }
            });
            observer.Start();
            ready.SetResult();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Volatile.Read(ref delivered) == 5));
            await TerraBaseTestServer.ReplicatedAsync(sender);
            await Assert.That(mostUnacknowledgedBytes).IsGreaterThan(0UL);
            await Assert.That(mostUnacknowledgedBytes).IsLessThanOrEqualTo(credit);
        }
        finally
        {
            ready.TrySetResult();
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task A_database_upgraded_from_schema_version_1_reaches_the_peer_as_a_baseline()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-legacy-").FullName;
        var deepJson = "{\"nested\":" + new string('[', 15) + "0" + new string(']', 15) + "}";
        var longId = new string('a', 257);
        const string controlId = "bad\nkey";
        var skipped = new BaselineAdmissionLogs();
        try
        {
            ReplicationTenant tenant;
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                clientTls.Apply(settings);
                settings["data-directory"] = data;
            }))
            {
                tenant = await ReplicationTenant.CreateAsync(seed, "legacy");
                await seed.Services.GetRequiredService<IStorageEngine>().DeleteDatabaseAsync(tenant.Database("legacy"), CancellationToken.None);
                Directory.CreateDirectory(Path.GetDirectoryName(tenant.File(seed, "legacy"))!);
                // Legacy documents within the shared admission contract migrate and replicate unchanged.
                await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DocumentApi.ConnectionString(tenant.File(seed, "legacy")));
                await connection.OpenAsync();
                await using var schema = connection.CreateCommand();
                schema.CommandText = """
                    CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY (collection, id)) WITHOUT ROWID;
                    INSERT INTO documents VALUES ('people', 'old', 3, '{"name":"from version 1"}');
                    INSERT INTO documents VALUES ('people', 'compat', 4, '{"x":1e307,"y":2}');
                    INSERT INTO documents VALUES ('people', 'deep', 5, $deep);
                    INSERT INTO documents VALUES ('people', 'wide', 5, '{"blob":"' || replace(hex(zeroblob(375000)), '00', 'ab') || '"}');
                    INSERT INTO documents VALUES ('people', 'huge', 1, '{"blob":"' || replace(hex(zeroblob(1572864)), '00', 'ab') || '"}');
                    INSERT INTO documents VALUES ('people', 'over-document-limit', 1, '{"blob":"' || replace(hex(zeroblob(786432)), '00', 'ab') || '"}');
                    INSERT INTO documents VALUES ('people', 'duplicate', 1, '{"x":1,"x":2}');
                    INSERT INTO documents VALUES ('people', $longId, 1, '{}');
                    INSERT INTO documents VALUES ('people', $controlId, 1, '{}');
                    INSERT INTO documents VALUES ('BadCollection', 'bad-collection', 1, '{}');
                    PRAGMA user_version = 1;
                    """;
                schema.Parameters.AddWithValue("$deep", deepJson);
                schema.Parameters.AddWithValue("$longId", longId);
                schema.Parameters.AddWithValue("$controlId", controlId);
                await schema.ExecuteNonQueryAsync();
            }

            await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
            });
            await tenant.BindAsync(receiver);
            using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read", "shapes:read"));
            await using var upgraded = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["data-directory"] = data;
                settings["peer-addresses"] = receiver.PeerAddress!.ToString();
            }, services => services.AddSingleton<ILoggerProvider>(skipped));

            await TerraBaseTestServer.ReplicatedAsync(upgraded);
            await Assert.That(skipped.Skipped).IsEqualTo(6);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            var database = tenant.Database("legacy");
            var source = (await upgraded.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
            var target = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
            long baselineHead;
            ReadOnlyMemory<byte> origin;
            await using (var local = await source.BeginTransactionAsync(write: false, deadline.Token))
            await using (var remote = await target.BeginTransactionAsync(write: false, deadline.Token))
            {
                baselineHead = await local.GetLastChangeSequenceAsync(deadline.Token);
                await Assert.That(baselineHead).IsEqualTo(1);
                var baseline = (await local.ReadChangesAsync(baselineHead, deadline.Token))!;
                var decoded = Features.Replication.ChangeGroup.Decode(Features.Replication.ChangeGroup.Encode(baseline));
                await Assert.That(decoded.Select(change => change.Id)).IsEquivalentTo(["old", "compat", "deep", "wide"]);
                origin = await local.GetOriginAsync(deadline.Token);
                await Assert.That(await remote.GetAppliedSequenceAsync(origin, deadline.Token)).IsEqualTo(baselineHead);
                foreach (var legacyId in new[] { "old", "compat", "deep", "wide" })
                {
                    var original = (await local.GetAsync("people", legacyId, deadline.Token))!;
                    var replicated = (await remote.GetAsync("people", legacyId, deadline.Token))!;
                    await Assert.That(replicated.Version).IsEqualTo(original.Version);
                    await Assert.That(replicated.Json.Span.SequenceEqual(original.Json.Span)).IsTrue();
                }

                foreach (var skippedId in new[] { "huge", "over-document-limit", "duplicate", longId, controlId })
                {
                    await Assert.That(await local.GetAsync("people", skippedId, deadline.Token)).IsNotNull();
                    await Assert.That(await remote.GetAsync("people", skippedId, deadline.Token)).IsNull();
                }

                await Assert.That(await local.GetAsync("BadCollection", "bad-collection", deadline.Token)).IsNotNull();
                await Assert.That(await remote.GetAsync("BadCollection", "bad-collection", deadline.Token)).IsNull();
                await Assert.That((await local.GetAsync("people", "over-document-limit", deadline.Token))!.Json.Length)
                    .IsBetween(1024 * 1024 + 1, IStorageTransaction.MaxChangeGroupBytes - 1);
                await Assert.That(System.Text.Encoding.UTF8.GetString((await remote.GetAsync("people", "deep", deadline.Token))!.Json.Span)).IsEqualTo(deepJson);
            }

            using var upgradedReader = clientTls.CreateClient(upgraded.ClientAddress!, await tenant.KeyAsync(upgraded, "documents:read", "shapes:read"));
            var collection = $"{tenant.Path("legacy")}/collections/people";
            string[] comparison = ["$id", "deep"];
            string[] projection = ["/nested"];
            HttpClient[] clients = [upgradedReader, reader];
            foreach (var client in clients)
            {
                var document = await ReadAsync(client, HttpMethod.Get, $"{collection}/documents/deep");
                await Assert.That(document.GetProperty("document").GetRawText()).IsEqualTo(deepJson);
                var page = await ReadAsync(client, HttpMethod.Get, $"{collection}/documents");
                await Assert.That(page.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "deep")
                    .GetProperty("document").GetRawText()).IsEqualTo(deepJson);
                var query = await ReadAsync(client, HttpMethod.Post, $"{collection}/query", JsonContent.Create(new
                {
                    filter = new { eq = comparison },
                    project = projection,
                }));
                await Assert.That(query.GetProperty("items").EnumerateArray().Single().GetProperty("document").GetRawText()).IsEqualTo(deepJson);
                using var redirect = await client.GetAsync(new Uri($"{collection}/shape?offset=-1", UriKind.Relative), deadline.Token);
                await Assert.That(redirect.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
                var snapshot = await ReadAsync(client, HttpMethod.Get, redirect.Headers.Location!.OriginalString);
                await Assert.That(snapshot.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray())
                    .Single(op => op.GetProperty("id").GetString() == "deep").GetProperty("document").GetRawText()).IsEqualTo(deepJson);
            }

            // An incompatible legacy value must not block a following normal client write.
            using var writer = clientTls.CreateClient(upgraded.ClientAddress!, await tenant.KeyAsync(upgraded, "documents:write"));
            using var rejected = await writer.PutAsync(new Uri($"{collection}/documents/{DocumentApi.NewId()}", UriKind.Relative),
                new StringContent("{\"nested\":" + new string('[', 80) + "0" + new string(']', 80) + "}", System.Text.Encoding.UTF8, "application/json"), deadline.Token);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            var next = Guid.CreateVersion7().ToString("D");
            using var added = await writer.PutAsJsonAsync(new Uri($"{tenant.Path("legacy")}/collections/people/documents/{next}", UriKind.Relative), new { after = "legacy" }, deadline.Token);
            added.EnsureSuccessStatusCode();
            await TerraBaseTestServer.ReplicatedAsync(upgraded);
            await Assert.That(await VersionAsync(tenant, receiver, "legacy", "people", next)).IsEqualTo(new DocumentVersion(6, Deleted: false));
            await using (var local = await source.BeginTransactionAsync(write: false, deadline.Token))
            await using (var remote = await target.BeginTransactionAsync(write: false, deadline.Token))
            {
                var head = await local.GetLastChangeSequenceAsync(deadline.Token);
                await Assert.That(head).IsEqualTo(baselineHead + 1);
                await Assert.That(await remote.GetAppliedSequenceAsync(origin, deadline.Token)).IsEqualTo(head);
                await Assert.That((await remote.GetAsync("people", next, deadline.Token))!.Json.Span.SequenceEqual("{\"after\":\"legacy\"}"u8)).IsTrue();
            }
            using var huge = await upgradedReader.GetAsync(new Uri($"{tenant.Path("legacy")}/collections/people/documents/huge", UriKind.Relative));

            // The oversized document stays readable where it is and is left out of the baseline, not the whole database.
            await Assert.That(await VersionAsync(tenant, receiver, "legacy", "people", "old")).IsEqualTo(new DocumentVersion(3, Deleted: false));
            await Assert.That(huge.IsSuccessStatusCode).IsTrue();
            using var oversized = await upgradedReader.GetAsync(new Uri($"{collection}/documents/over-document-limit", UriKind.Relative), deadline.Token);
            oversized.EnsureSuccessStatusCode();
            using var duplicate = await upgradedReader.GetAsync(new Uri($"{collection}/documents/duplicate", UriKind.Relative), deadline.Token);
            duplicate.EnsureSuccessStatusCode();

            // A shallow patch can remove legacy nesting, after which strict client validation accepts the result.
            using var patcher = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:write"));
            using var patched = await patcher.PatchAsync(new Uri($"{collection}/documents/deep", UriKind.Relative),
                new StringContent("{\"nested\":null}", System.Text.Encoding.UTF8, "application/merge-patch+json"), deadline.Token);
            patched.EnsureSuccessStatusCode();
            var shallow = await ReadAsync(reader, HttpMethod.Get, $"{collection}/documents/deep");
            await Assert.That(shallow.GetProperty("document").GetRawText()).IsEqualTo("{}");

            async Task<JsonElement> ReadAsync(HttpClient client, HttpMethod method, string path, HttpContent? content = null)
            {
                using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
                using var response = await client.SendAsync(request, deadline.Token);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token), new JsonDocumentOptions
                {
                    MaxDepth = Features.Documents.RequestJson.StoredDocumentMaxDepth + 8,
                });
                return json.RootElement.Clone();
            }
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    private sealed class BaselineAdmissionLogs : ILoggerProvider, ILogger
    {
        private int _skipped;

        public int Skipped => Volatile.Read(ref _skipped);

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values
                && values.SingleOrDefault(pair => pair.Key == "Skipped").Value is int count)
            {
                Interlocked.Add(ref _skipped, count);
            }
        }

        public void Dispose()
        {
        }
    }

    private static async Task CreateCollectionAsync(ReplicationTenant tenant, HttpClient client, string database, string collection)
    {
        using var created = await client.PutAsync(new Uri(tenant.Path(database), UriKind.Relative), null);
        created.EnsureSuccessStatusCode();
        using var response = await client.PostAsJsonAsync(new Uri($"{tenant.Path(database)}/collections", UriKind.Relative), new { name = collection });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<DocumentVersion> VersionAsync(ReplicationTenant tenant, TerraBaseTestServer node, string database, string collection, string id)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var name = tenant.Database(database);
        var store = await node.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(name, deadline.Token);
        await using var transaction = await store!.BeginTransactionAsync(write: false, deadline.Token);
        return await transaction.GetVersionAsync(collection, id, deadline.Token)
            ?? throw new InvalidDataException("the document version is missing");
    }

    private static void Configure(Dictionary<string, string> settings, PeerTls peerTls, ClientTls clientTls, int port, int peerPort)
    {
        peerTls.Apply(settings);
        clientTls.Apply(settings);
        settings["peer-port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings["peer-addresses"] = $"https://localhost:{peerPort}";
    }

    private static async Task PutAsync(ReplicationTenant tenant, HttpClient client, string id)
    {
        var person = new { name = Faker.Name.FullName(), email = Faker.Internet.Email(), bio = Faker.Lorem.Paragraph() };
        using var response = await client.PutAsJsonAsync(new Uri($"{tenant.Path("shop")}/collections/people/documents/{ReplicationDocuments.Id(id)}", UriKind.Relative), person);
        response.EnsureSuccessStatusCode();
    }

    private static async Task DeleteAsync(ReplicationTenant tenant, HttpClient client, string id)
    {
        using var response = await client.DeleteAsync(new Uri($"{tenant.Path("shop")}/collections/people/documents/{ReplicationDocuments.Id(id)}", UriKind.Relative));
        response.EnsureSuccessStatusCode();
    }

    // A realistic order: repeated keys and similar line items, as JSON documents usually are.
    private static async Task PutOrderAsync(ReplicationTenant tenant, HttpClient client, string id)
    {
        var order = new
        {
            customer = new { name = Faker.Name.FullName(), email = Faker.Internet.Email(), address = Faker.Address.FullAddress() },
            status = "paid",
            lines = Enumerable.Range(0, 25).Select(_ => new
            {
                sku = Faker.Commerce.Ean13(),
                product = Faker.Commerce.ProductName(),
                department = Faker.Commerce.Department(),
                quantity = Faker.Random.Int(1, 5),
                unitPrice = Faker.Random.Decimal(1, 500),
                currency = "EUR",
            }),
        };
        using var response = await client.PutAsJsonAsync(new Uri($"{tenant.Path("orders")}/collections/orders/documents/{ReplicationDocuments.Id(id)}", UriKind.Relative), order);
        response.EnsureSuccessStatusCode();
    }

    // Creates a management resource (or, without a name, reads one) and returns one property of it.
    private static async Task<string> CreateAsync(HttpClient dashboard, string? organization, string path, string? name, string property)
    {
        using var request = new HttpRequestMessage(name is null ? HttpMethod.Get : HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (name is not null)
        {
            request.Content = JsonContent.Create(new { name });
        }

        if (organization is not null)
        {
            request.Headers.Add("X-TerraBase-Organization-Id", organization);
        }

        using var response = await dashboard.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty(property).GetString()!;
    }

    private static Task<List<(string Id, string Data)>> ListAsync(ReplicationTenant tenant, HttpClient client, string database) =>
        ListAsync(tenant, client, database, database);

    private static async Task<List<(string Id, string Data)>> ListAsync(
        ReplicationTenant tenant, HttpClient client, string database, string collection, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(new Uri($"{tenant.Path(database)}/collections/{collection}/documents?limit=100", UriKind.Relative), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (error.RootElement.GetProperty("error").GetProperty("code").GetString() == "unknown_database")
            {
                return [];
            }
        }

        response.EnsureSuccessStatusCode();
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return [.. page.RootElement.GetProperty("items").EnumerateArray().Select(document => (
            document.GetProperty("id").GetString()!,
            document.GetProperty("document").GetRawText()))];
    }
}
