using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Fast.Documents;

namespace TerraScale.TerraBase.Fast.Storage;

public sealed class StorageHistoryTests
{
    [Test]
    public async Task Storage_refuses_an_empty_document_id()
    {
        // A snapshot resumes strictly after an id; the empty id would have none before it, so no writer may store it.
        await using var api = await DocumentApi.StartAsync();
        var store = await StoreAsync(api);
        await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await Assert.ThrowsAsync<StorageWriteLimitException>(() => writer.PutAsync(DocumentApi.Collection, "", Encoding.UTF8.GetBytes("{}"), CancellationToken.None).AsTask());
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
    public async Task Upgrading_keeps_system_records_out_of_shape_history()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-upgrade-").FullName;
        try
        {
            // A data directory as a schema-4 build (TS-475) left it: start a server once, then take _system back to
            // version 4 with a Management-like record in it.
            await using (await DocumentApi.StartAsync(settings => settings["data-directory"] = directory))
            {
            }

            var path = Path.Combine(directory, "_system.db");
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP TABLE change_log;
                    DROP TABLE shape_history;
                    ALTER TABLE collections DROP COLUMN generation;
                    INSERT INTO collections VALUES ('invitations', 1);
                    INSERT INTO documents VALUES ('invitations', 'a', 1, '{"email":"someone@example.com"}');
                    PRAGMA user_version = 4;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var engine = new TerraScale.TerraBase.Engine.Sqlite.SqliteStorageEngine(
                directory, TimeSpan.FromSeconds(5), Microsoft.Extensions.Logging.Abstractions.NullLogger<TerraScale.TerraBase.Engine.Sqlite.SqliteStorageEngine>.Instance,
                TimeSpan.FromSeconds(1)))
            {
                await engine.OpenSystemDatabaseAsync(CancellationToken.None);
            }

            // Migrated to version 5, but _system's deletes never reach history, so its records are not copied there.
            await Assert.That(await HistoryRowsAsync(path)).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<long> HistoryRowsAsync(string path)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM change_log;";
        return (long)(await command.ExecuteScalarAsync())!;
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
    public async Task Storage_commit_bytes_include_repeated_writes_and_shape_framing(bool repeated)
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

        await using (var rejected = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            for (var i = 0; i < 6; i++)
            {
                await rejected.PutAsync(DocumentApi.Collection, ids[repeated ? 0 : i], document, CancellationToken.None);
            }

            // Seven raw 1 MiB documents leave no room for their operation framing.
            await Assert.ThrowsAsync<StorageWriteLimitException>(() => rejected.PutAsync(DocumentApi.Collection, ids[repeated ? 0 : 6], document, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.CommitAsync(CancellationToken.None).AsTask());
        }

        await Assert.That((await api.GetAsync(ids[0])).Status).IsEqualTo(HttpStatusCode.NotFound);
        using var unchanged = await shapes.GetAsync(new Uri($"{shape}?handle={handle}&offset={head}", UriKind.Relative));
        using var unchangedBody = JsonDocument.Parse(await unchanged.Content.ReadAsStringAsync());
        await Assert.That(unchangedBody.RootElement.GetProperty("next_offset").GetString()).IsEqualTo(head);
        await Assert.That(unchangedBody.RootElement.GetProperty("changes").GetArrayLength()).IsEqualTo(0);

        long committed;
        await using (var accepted = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            for (var i = 0; i < 6; i++)
            {
                await accepted.PutAsync(DocumentApi.Collection, ids[repeated ? 0 : i], document, CancellationToken.None);
            }

            committed = await accepted.CommitAsync(CancellationToken.None);
        }

        using var log = await shapes.GetAsync(new Uri($"{shape}?handle={handle}&offset={head}", UriKind.Relative));
        log.EnsureSuccessStatusCode();
        var bytes = await log.Content.ReadAsByteArrayAsync();
        using var body = JsonDocument.Parse(bytes);
        await Assert.That(bytes.Length).IsLessThanOrEqualTo(8 * 1024 * 1024);
        // Repeated writes of one document in one commit count toward the limit, but the chunk carries its final state once.
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
            await Assert.ThrowsAsync<StorageWriteLimitException>(() => oversized.PutAsync(DocumentApi.Collection, id, large, CancellationToken.None).AsTask());
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
            await Assert.ThrowsAsync<DecoderFallbackException>(() => writer.PutAsync(DocumentApi.Collection, id, invalid, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.PutAsync(DocumentApi.Collection, id, invalid, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
        }

        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static async Task<IDatabaseStore> StoreAsync(DocumentApi api)
    {
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        return (await engine.OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
    }
}
