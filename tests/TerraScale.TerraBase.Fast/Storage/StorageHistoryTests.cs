using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Storage;

public sealed class StorageHistoryTests
{
    [Test]
    public async Task A_dropped_collection_has_state_after_its_replication_journal_is_cleared()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var created = await api.SendAsync(HttpMethod.Post, api.Documents, new { n = 1 }, cancellationToken: token);
        created.Status.ShouldBe(HttpStatusCode.Created);
        var id = created.Json.GetProperty("id").GetString()!;
        var path = await api.DatabaseFileAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(
            await api.TenantOfAsync(cancellationToken: token), token))!;
        (await api.SendAsync(HttpMethod.Delete, api.Collections + "/" + DocumentApi.Collection, cancellationToken: token))
            .Status.ShouldBe(HttpStatusCode.OK);

        await StorageFixture.ExecuteAsync(path, "DELETE FROM changes;");
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(path)))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM change_log WHERE collection = $collection AND body IS NULL;";
            command.Parameters.AddWithValue("$collection", DocumentApi.Collection);
            await Assert.That((long)(await command.ExecuteScalarAsync(token))!).IsGreaterThan(0);
        }

        await using var reader = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await reader.GetAsync(DocumentApi.Collection, id, token)).IsNull();
        await Assert.That(await reader.GetCollectionAsync(DocumentApi.Collection, token)).IsNull();
        await Assert.That(await reader.HasCollectionStateAsync(DocumentApi.Collection, prefix: false, token)).IsTrue();
        await Assert.That(await reader.HasCollectionStateAsync("ite", prefix: true, token)).IsTrue();
        await Assert.That(await reader.HasCollectionStateAsync("item", prefix: false, token)).IsFalse();
        await Assert.That(await reader.HasCollectionStateAsync("other", prefix: true, token)).IsFalse();
    }

    [Test]
    [Arguments("unicode-\uffff", "unicode-\U00010000")]
    [Arguments("unicode-\ud7ff", "unicode-\ue000")]
    [Arguments("unicode-\U00010000", "unicode-\U00010001")]
    [Arguments("unicode-\U0010ffff", "unicode.")]
    [Arguments("\U0010ffff", "\uffff")]
    public async Task System_collection_state_prefixes_follow_Unicode_scalar_boundaries(string prefix, string outside)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var id = Ulid.NewUlid().ToString();
        await using (var writer = await system.BeginTransactionAsync(write: true, token))
        {
            await writer.PutAsync(outside, id, "{}"u8.ToArray(), token);
            await writer.CommitAsync(token);
        }

        await using (var reader = await system.BeginTransactionAsync(write: false, token))
        {
            await Assert.That(await reader.HasCollectionStateAsync(prefix, prefix: true, token)).IsFalse();
        }

        await using (var writer = await system.BeginTransactionAsync(write: true, token))
        {
            await writer.PutAsync(prefix + "/child", id, "{}"u8.ToArray(), token);
            await writer.CommitAsync(token);
        }

        await using (var reader = await system.BeginTransactionAsync(write: false, token))
        {
            await Assert.That(await reader.HasCollectionStateAsync(prefix, prefix: false, token)).IsFalse();
            await Assert.That(await reader.HasCollectionStateAsync(prefix, prefix: true, token)).IsTrue();
        }

        await using (var writer = await system.BeginTransactionAsync(write: true, token))
        {
            await writer.PutAsync(prefix, id, "{}"u8.ToArray(), token);
            await writer.CommitAsync(token);
        }

        await using var after = await system.BeginTransactionAsync(write: false, token);
        await Assert.That(await after.HasCollectionStateAsync(prefix, prefix: false, token)).IsTrue();
        await Assert.That(await after.HasCollectionStateAsync(prefix, prefix: true, token)).IsTrue();
    }

    [Test]
    [Arguments(false, "complete")]
    [Arguments(true, "complete")]
    [Arguments(false, "break")]
    [Arguments(true, "break")]
    [Arguments(false, "cancel")]
    [Arguments(true, "cancel")]
    public async Task An_active_changes_stream_refuses_overlap_and_releases_its_transaction(bool native, string ending)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-threads"] = "1", cancellationToken: token);
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 }, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(id, new { n = 2 }, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.OK);
        var engine = native ? api.Server.Services.GetRequiredService<SqliteStorageEngine>()
            : api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(await api.TenantOfAsync(cancellationToken: token), token))!;
        await using var transaction = await store.BeginTransactionAsync(write: false, token);
        var offset = await transaction.GetOffsetAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using (var changes = transaction.ChangesAsync(DocumentApi.Collection, null, 0, offset, cancellation.Token).GetAsyncEnumerator())
        {
            await Assert.That(await changes.MoveNextAsync()).IsTrue();
            await Assert.That(changes.Current.Id).IsEqualTo(id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.GetOffsetAsync(token).AsTask().WaitAsync(token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.DisposeAsync().AsTask().WaitAsync(token));
            await using (var overlapping = transaction.ChangesAsync(DocumentApi.Collection, null, 0, offset, token).GetAsyncEnumerator())
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => overlapping.MoveNextAsync().AsTask().WaitAsync(token));
            }
            if (ending == "complete")
            {
                await Assert.That(await changes.MoveNextAsync()).IsTrue();
                await Assert.That(await changes.MoveNextAsync()).IsFalse();
                await Assert.That(await transaction.GetOffsetAsync(token)).IsEqualTo(offset);
            }
            else if (ending == "cancel")
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => changes.MoveNextAsync().AsTask().WaitAsync(token));
            }
        }

        await Assert.That(await transaction.GetOffsetAsync(token)).IsEqualTo(offset);
        await transaction.DisposeAsync();
        await using var reused = await store.BeginTransactionAsync(write: false, token);
        await Assert.That((await reused.GetAsync(DocumentApi.Collection, id, token))?.Version).IsEqualTo(offset);
        (await api.GetAsync(id, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_write_refused_during_a_changes_stream_cannot_commit_earlier_writes(bool native)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var seed = DocumentApi.NewId();
        (await api.PutAsync(seed, new { n = 1 }, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        var engine = native ? api.Server.Services.GetRequiredService<SqliteStorageEngine>()
            : api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(await api.TenantOfAsync(cancellationToken: token), token))!;
        var idA = DocumentApi.NewId();
        var idB = DocumentApi.NewId();
        long offset;
        await using (var writer = await store.BeginTransactionAsync(write: true, token))
        {
            offset = await writer.GetOffsetAsync(token);
            await writer.PutAsync(DocumentApi.Collection, idA, Encoding.UTF8.GetBytes("{}"), token);
            await using (var changes = writer.ChangesAsync(DocumentApi.Collection, null, 0, long.MaxValue, token).GetAsyncEnumerator())
            {
                await Assert.That(await changes.MoveNextAsync()).IsTrue();
                await Assert.ThrowsAsync<InvalidOperationException>(() => writer.PutAsync(DocumentApi.Collection, idB, Encoding.UTF8.GetBytes("{}"), token).AsTask().WaitAsync(token));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(token).AsTask());
        }
        await using var reader = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await reader.GetOffsetAsync(token)).IsEqualTo(offset);
        await Assert.That(await reader.GetAsync(DocumentApi.Collection, idA, token)).IsNull();
        await Assert.That(await reader.GetAsync(DocumentApi.Collection, idB, token)).IsNull();
        (await api.GetAsync(seed, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Storage_refuses_an_empty_document_id()
    {
        // A snapshot resumes strictly after an id; the empty id would have none before it, so no writer may store it.
        await using var api = await DocumentApi.StartAsync();
        var store = await StoreAsync(api);
        await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.PutAsync(DocumentApi.Collection, "", Encoding.UTF8.GetBytes("{}"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
    }

    [Test]
    public async Task A_write_below_the_documents_api_creates_missing_collection_metadata_and_keeps_its_history()
    {
        // Engine callers may write to a collection they never created: its metadata is created so history is never dropped.
        await using var api = await DocumentApi.StartAsync();
        var store = await StoreAsync(api);
        var id = DocumentApi.NewId();
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await writer.PutAsync("unlisted", id, Encoding.UTF8.GetBytes("{}"), CancellationToken.None);
            await writer.CommitAsync(CancellationToken.None);
        }

        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var changes = new List<StoredDocument>();
        await foreach (var change in reader.ChangesAsync("unlisted", null, 0, long.MaxValue, CancellationToken.None))
        {
            changes.Add(change);
        }

        await Assert.That(await reader.GetCollectionAsync("unlisted", CancellationToken.None)).IsNotNull();
        await Assert.That(changes.Select(change => change.Id)).IsEquivalentTo([id]);
    }

    [Test]
    public async Task The_system_database_keeps_no_shape_history()
    {
        // Clients cannot name _system, so its writes (API keys, Management) carry no shape history and no
        // per-commit shape limits; Management cleanup may delete more than 1024 entries in one transaction.
        await using var api = await DocumentApi.StartAsync();
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var system = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var writer = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            foreach (var id in Enumerable.Range(0, 1100))
            {
                await writer.PutAsync("bulk", id.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetBytes("{}"), CancellationToken.None);
            }

            await writer.CommitAsync(CancellationToken.None);
        }

        await using var reader = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        var changes = 0;
        await foreach (var _ in reader.ChangesAsync("bulk", null, 0, long.MaxValue, CancellationToken.None))
        {
            changes++;
        }

        await Assert.That(await reader.GetAsync("bulk", "1099", CancellationToken.None)).IsNotNull();
        await Assert.That(changes).IsEqualTo(0);
    }

    [Test]
    public async Task Restart_keeps_system_records_out_of_shape_history()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-system-history-").FullName;
        var id = EntityIds.New().ToString();
        try
        {
            await using (var api = await DocumentApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
                await using var writer = await system.BeginTransactionAsync(write: true, CancellationToken.None);
                await writer.PutAsync("invitations", id, "{\"email\":\"someone@example.com\"}"u8.ToArray(), CancellationToken.None);
                await writer.CommitAsync(CancellationToken.None);
            }

            await using (var api = await DocumentApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
                await using var reader = await system.BeginTransactionAsync(write: false, CancellationToken.None);
                await Assert.That(await reader.GetAsync("invitations", id, CancellationToken.None)).IsNotNull();
            }

            await Assert.That(await HistoryRowsAsync(Path.Combine(directory, "_system.db"), CancellationToken.None)).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Restart_preserves_snapshot_births_and_later_history()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-history-restart-").FullName;
        try
        {
            var (early, late, revised) = (DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId());
            TenantContext database;
            long offset;
            await using (var api = await DocumentApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                (await api.PutAsync(early, new { status = "open" })).Status.ShouldBe(HttpStatusCode.Created);
                (await api.PutAsync(revised, new { status = "first" })).Status.ShouldBe(HttpStatusCode.Created);
                var written = await api.TransactAsync(new { op = "put", collection = DocumentApi.Collection, id = revised, document = new { status = "at-offset" } });
                offset = long.Parse(written.Json.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture);
                (await api.PutAsync(late, new { status = "later" })).Status.ShouldBe(HttpStatusCode.Created);
                await api.TransactAsync(new { op = "put", collection = DocumentApi.Collection, id = revised, document = new { status = "later" } });
                database = await api.TenantOfAsync();
            }

            await using var reopened = await DocumentApi.StartAsync(
                settings => settings["data-directory"] = directory, StorageTransactionTests.ConfigurePool);
            var engine = reopened.Server.Services.GetRequiredService<IStorageEngine>();
            var store = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
            var after = DocumentApi.NewId();
            await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                await writer.PutAsync(DocumentApi.Collection, after, Encoding.UTF8.GetBytes("{}"), CancellationToken.None);
                await writer.CommitAsync(CancellationToken.None);
            }

            await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            var query = new DocumentQuery(DocumentApi.Collection, null, [], null, 1000, long.MaxValue);
            var atOffset = await reader.SnapshotAsync(query, offset, CancellationToken.None);
            var atHead = await reader.SnapshotAsync(query, await reader.GetOffsetAsync(CancellationToken.None), CancellationToken.None);

            // Persisted births keep earlier snapshots intact while new writes extend the history.
            await Assert.That(atOffset.Rows.Select(row => row.Document.Id)).IsEquivalentTo([early, revised]);
            await Assert.That(Encoding.UTF8.GetString(atOffset.Rows.Single(row => row.Document.Id == revised).Document.Json.Span)).Contains("at-offset");
            await Assert.That(atHead.Rows.Select(row => row.Document.Id)).IsEquivalentTo([early, revised, late, after]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<long> HistoryRowsAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM change_log;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    [Test]
    public async Task Assertion_only_transactions_do_not_notify_commit_waiters()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = await StoreAsync(api);
        var sequence = store.CommitSequence;
        using var cancelled = new CancellationTokenSource();
        var waiting = store.WaitForCommitAsync(sequence, cancelled.Token);
        var id = DocumentApi.NewId();
        var asserted = await api.TransactAsync(new { op = "assert", collection = DocumentApi.Collection, id, absent = true });
        await Assert.That(asserted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(store.CommitSequence).IsEqualTo(sequence);
        await Assert.That(waiting.IsCompleted).IsFalse();

        await api.PutAsync(id, new { value = 1 });
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(store.CommitSequence).IsEqualTo(sequence + 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Storage_commit_limits_bound_distinct_states_and_count_repeated_writes_with_framing(bool repeated)
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var store = await StoreAsync(api);
        var ids = Enumerable.Range(0, 7).Select(_ => DocumentApi.NewId()).ToArray();
        var document = Encoding.UTF8.GetBytes("{\"v\":\"" + new string('x', (1 << 20) - 8) + "\"}");
        await Assert.That(document.Length).IsEqualTo(1 << 20);
        var shape = api.Shape;
        using var start = await shapes.GetAsync(new Uri(shape + "?offset=-1", UriKind.Relative));
        var handle = start.Headers.GetValues("TB-Shape-Handle").Single();
        using var snapshot = await shapes.GetAsync(start.Headers.Location!);
        using var snapshotBody = JsonDocument.Parse(await snapshot.Content.ReadAsStringAsync());
        var head = snapshotBody.RootElement.GetProperty("next_offset").GetString();
        var sequence = store.CommitSequence;
        using var cancelled = new CancellationTokenSource();
        var waiting = store.WaitForCommitAsync(sequence, cancelled.Token);

        await using (var rejected = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            for (var i = 0; i < (repeated ? 6 : 1); i++)
            {
                await rejected.PutAsync(DocumentApi.Collection, ids[repeated ? 0 : i], document, CancellationToken.None);
            }

            if (repeated)
            {
                // Capture holds one final state, but seven raw 1 MiB writes leave no room for shape framing.
                await Assert.ThrowsAsync<StorageWriteLimitException>(() => rejected.PutAsync(DocumentApi.Collection, ids[0], document, CancellationToken.None).AsTask());
            }
            else
            {
                // Two distinct 1 MiB states plus their names and ids exceed capture's 2 MiB bound first.
                await Assert.ThrowsAsync<ChangeGroupCapacityException>(() => rejected.PutAsync(DocumentApi.Collection, ids[1], document, CancellationToken.None).AsTask());
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.CommitAsync(CancellationToken.None).AsTask());
        }

        await Assert.That((await api.GetAsync(ids[0])).Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await api.GetAsync(ids[1])).Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(store.CommitSequence).IsEqualTo(sequence);
        await Assert.That(waiting.IsCompleted).IsFalse();
        using var unchanged = await shapes.GetAsync(new Uri($"{shape}?handle={handle}&offset={head}", UriKind.Relative));
        using var unchangedBody = JsonDocument.Parse(await unchanged.Content.ReadAsStringAsync());
        await Assert.That(unchanged.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(unchangedBody.RootElement.GetProperty("next_offset").GetString()).IsEqualTo(head);
        await Assert.That(unchangedBody.RootElement.GetProperty("changes").GetArrayLength()).IsEqualTo(0);

        // Six smaller distinct documents fit capture; six full-size writes of one key retain only its final state.
        var acceptedDocument = repeated ? document : Encoding.UTF8.GetBytes("{\"v\":\"" + new string('x', 300 * 1024) + "\"}");
        long committed;
        await using (var accepted = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            for (var i = 0; i < 6; i++)
            {
                await accepted.PutAsync(DocumentApi.Collection, ids[repeated ? 0 : i], acceptedDocument, CancellationToken.None);
            }

            committed = await accepted.CommitAsync(CancellationToken.None);
        }

        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(store.CommitSequence).IsEqualTo(sequence + 1);
        await Assert.That(committed).IsEqualTo(long.Parse(head!, CultureInfo.InvariantCulture) + 1);
        using var log = await shapes.GetAsync(new Uri($"{shape}?handle={handle}&offset={head}", UriKind.Relative));
        log.EnsureSuccessStatusCode();
        var bytes = await log.Content.ReadAsByteArrayAsync();
        using var body = JsonDocument.Parse(bytes);
        await Assert.That(bytes.Length).IsLessThanOrEqualTo(8 * 1024 * 1024);
        await Assert.That(body.RootElement.GetProperty("changes").GetArrayLength()).IsEqualTo(1);
        // Repeated writes count toward shape framing, but capture and the chunk retain one final state per key.
        await Assert.That(body.RootElement.GetProperty("changes")[0].GetProperty("ops").GetArrayLength()).IsEqualTo(repeated ? 1 : 6);
        await Assert.That(body.RootElement.GetProperty("next_offset").GetString()).IsEqualTo(committed.ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task Storage_write_count_and_document_size_failures_cannot_commit()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = await StoreAsync(api);
        var id = DocumentApi.NewId();
        var small = Encoding.UTF8.GetBytes("{}");
        await using (var repeated = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            for (var i = 0; i < 512; i++)
            {
                await repeated.PutAsync(DocumentApi.Collection, id, small, CancellationToken.None);
                await repeated.DeleteAsync(DocumentApi.Collection, id, CancellationToken.None);
            }

            await Assert.ThrowsAsync<StorageWriteLimitException>(() => repeated.PutAsync(DocumentApi.Collection, id, small, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => repeated.CommitAsync(CancellationToken.None).AsTask());
        }

        await using (var oversized = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await oversized.PutAsync(DocumentApi.Collection, id, small, CancellationToken.None);
            var large = Encoding.UTF8.GetBytes("{\"v\":\"" + new string('x', 1 << 20) + "\"}");
            var error = await Assert.ThrowsAsync<DocumentValidationException>(() => oversized.PutAsync(DocumentApi.Collection, id, large, CancellationToken.None).AsTask());
            await Assert.That(error!.TooLarge).IsTrue();
            await Assert.ThrowsAsync<InvalidOperationException>(() => oversized.CommitAsync(CancellationToken.None).AsTask());
        }

        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Invalid_utf8_is_rejected_before_lossy_expansion_and_cannot_commit()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = await StoreAsync(api);
        var id = DocumentApi.NewId();
        var invalid = new byte[1 << 20];
        invalid.AsSpan().Fill(0xff);
        Encoding.UTF8.GetBytes("{\"v\":\"").CopyTo(invalid, 0);
        invalid[^2] = (byte)'"';
        invalid[^1] = (byte)'}';
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await writer.PutAsync(DocumentApi.Collection, id, Encoding.UTF8.GetBytes("{}"), CancellationToken.None);
            // A lossy decoder triples these bytes, bypassing the admitted body size on repeated writes.
            await Assert.ThrowsAsync<DocumentValidationException>(() => writer.PutAsync(DocumentApi.Collection, id, invalid, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.PutAsync(DocumentApi.Collection, id, invalid, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
        }

        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static async Task<IDatabaseStore> StoreAsync(DocumentApi api, CancellationToken cancellationToken = default)
    {
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        return (await engine.OpenDatabaseAsync(await api.TenantOfAsync(cancellationToken: cancellationToken), cancellationToken))!;
    }
}
