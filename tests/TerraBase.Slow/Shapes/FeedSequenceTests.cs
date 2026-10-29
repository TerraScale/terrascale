using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Turso.Data.Sqlite;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class FeedSequenceTests
{
    [Test]
    public async Task Emptying_the_feed_by_retention_and_reopening_keeps_the_sequence_counter()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-feed-seq-").FullName;
        try
        {
            TenantContext tenant;
            long highest;
            await using (var api = await DocumentApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                _ = await api.PutAsync(DocumentApi.NewId(), new { n = 1 });
                _ = await api.PutAsync(DocumentApi.NewId(), new { n = 2 });
                tenant = await api.TenantOfAsync();
                var store = await StoreAsync(api.Server, tenant);
                var written = await FeedSeqsAsync(store);
                await Assert.That(written.Length).IsGreaterThan(0);
                highest = written[^1];
                await Assert.That(await CounterAsync(store)).IsEqualTo(highest);

                await Assert.That(await TrimAsync(store, FarFuture())).IsEqualTo(written.Length);
                await Assert.That(await FeedSeqsAsync(store)).IsEmpty();
                await Assert.That(await CounterAsync(store)).IsEqualTo(highest);
            }

            await using var restarted = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
            var reopened = await StoreAsync(restarted, tenant);
            await Assert.That(await FeedSeqsAsync(reopened)).IsEmpty();
            await Assert.That(await CounterAsync(reopened)).IsEqualTo(highest);
            await PutOneAsync(reopened, DocumentApi.NewId());
            await Assert.That((await FeedSeqsAsync(reopened)).Single()).IsEqualTo(highest + 1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task The_documents_of_one_transaction_take_contiguous_sequence_numbers_and_share_one_commit_offset()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var before = await CounterAsync(store);

        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            for (var n = 1; n <= 3; n++)
            {
                _ = await transaction.PutAsync(DocumentApi.Collection, DocumentApi.NewId(), Json(n), CancellationToken.None);
            }

            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        var added = (await FeedRowsAsync(store)).Where(row => row.Seq > before).ToArray();
        await Assert.That(added.Length).IsEqualTo(3);
        await Assert.That(added[^1].Seq - added[0].Seq).IsEqualTo(2L).Because("one commit's rows are contiguous");
        await Assert.That(added.Select(row => row.CommitOffset).Distinct().Count()).IsEqualTo(1)
            .Because("one commit's rows share its commit offset, so the sealer can cut on the boundary");
        await Assert.That(await CounterAsync(store)).IsEqualTo(before + 3);

        await PutOneAsync(store, DocumentApi.NewId());
        var (_, nextCommitOffset) = (await FeedRowsAsync(store)).Single(row => row.Seq == added[^1].Seq + 1);
        await Assert.That(nextCommitOffset).IsGreaterThan(added[0].CommitOffset).Because("a later commit takes a later offset");
    }

    [Test]
    public async Task A_rolled_back_transaction_leaves_no_gap_in_the_sequence()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var feedBefore = await FeedSeqsAsync(store);
        var before = await CounterAsync(store);
        await using (var abandoned = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await abandoned.PutAsync(DocumentApi.Collection, DocumentApi.NewId(), Json(1), CancellationToken.None);
            _ = await abandoned.PutAsync(DocumentApi.Collection, DocumentApi.NewId(), Json(2), CancellationToken.None);
            await ((TursoStorageTransaction)abandoned).FlushStatesAsync();

            var inside = await abandoned.ReadFeedAsync(null, null, 1000, long.MaxValue, CancellationToken.None);
            await Assert.That(inside.Events.Count).IsEqualTo(feedBefore.Length + 2)
                .Because("the uncommitted transaction has allocated and filed both documents' feed rows");
        }

        await Assert.That(await CounterAsync(store)).IsEqualTo(before);
        await Assert.That(await FeedSeqsAsync(store)).IsEquivalentTo(feedBefore);

        await PutOneAsync(store, DocumentApi.NewId());
        await Assert.That((await FeedSeqsAsync(store)).Except(feedBefore).Single()).IsEqualTo(before + 1);
        await Assert.That(await CounterAsync(store)).IsEqualTo(before + 1);
    }

    [Test]
    public async Task An_exhausted_sequence_fails_the_commit_with_overflow_and_stores_nothing()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        await RawWriteAsync(store, $"UPDATE _tb_feed_sequence SET seq = {long.MaxValue} WHERE id = 1;");
        var id = DocumentApi.NewId();
        await Assert.That(async () => await PutOneAsync(store, id)).Throws<OverflowException>();

        await Assert.That(await CounterAsync(store)).IsEqualTo(long.MaxValue);
        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await reader.GetAsync(DocumentApi.Collection, id, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Shape_rows_written_uncaptured_never_reach_change_capture()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var tenant = await api.TenantOfAsync();
        var store = await StoreAsync(api.Server, tenant);
        var document = DocumentApi.NewId();
        _ = await api.PutAsync(document, new { n = 1 });
        await RawWriteAsync(store, ShapeChunk, ShapeSnapshot);

        await Assert.That(await ScalarAsync(store, "SELECT COUNT(*) FROM turso_cdc WHERE table_name IN ('_tb_shape_chunk', '_tb_shape_snapshot');")).IsEqualTo(0L);

        await Assert.That(await ScalarAsync(store, "SELECT COUNT(*) FROM turso_cdc WHERE table_name = '" + TursoStorageTransaction.Table(DocumentApi.Collection).Trim('"') + "';")).IsGreaterThan(0L);
    }

    [Test]
    public async Task A_database_copy_keeps_the_feed_and_counter_but_not_shape_rows()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var source = await api.TenantOfAsync();
        var store = await StoreAsync(api.Server, source);
        _ = await api.PutAsync(DocumentApi.NewId(), new { n = 1 });
        await RawWriteAsync(store, ShapeChunk, ShapeSnapshot);

        var target = source with { DatabaseId = DocumentApi.NewId() };
        await Assert.That(await api.Server.Services.GetRequiredService<TursoStorageEngine>().CopyDatabaseAsync(source, target, CancellationToken.None)).IsTrue();

        var copy = await StoreAsync(api.Server, target);
        await Assert.That(await ScalarAsync(copy, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('_tb_shape_chunk', '_tb_shape_snapshot');")).IsEqualTo(2L)
            .Because("the copy rebuilds the shape tables' schema");
        await Assert.That(await ScalarAsync(copy, "SELECT COUNT(*) FROM _tb_shape_chunk;")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(copy, "SELECT COUNT(*) FROM _tb_shape_snapshot;")).IsEqualTo(0L);
        await Assert.That(await FeedSeqsAsync(copy)).IsEquivalentTo(await FeedSeqsAsync(store));
        await Assert.That(await CounterAsync(copy)).IsEqualTo(await CounterAsync(store));
    }

    [Test]
    public async Task A_backup_snapshot_keeps_the_feed_and_counter_but_empties_shape_rows()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-feed-snapshot-").FullName;
        try
        {
            await using var api = await DocumentApi.StartIsolatedAsync();
            var tenant = await api.TenantOfAsync();
            var store = await StoreAsync(api.Server, tenant);
            var faker = new Faker();
            _ = await api.PutAsync(DocumentApi.NewId(), new { text = faker.Lorem.Sentence() });
            await RawWriteAsync(store, ShapeChunk, ShapeSnapshot);
            var feed = await FeedSeqsAsync(store);
            var counter = await CounterAsync(store);

            var path = Path.Combine(directory, "snapshot.db");
            await using (var tap = (TursoChangeTap)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenTapAsync(tenant, CancellationToken.None))!)
            {
                _ = await tap.SnapshotAsync(Stream.Null, path, CancellationToken.None);
            }

            await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }));
            await connection.OpenAsync();
            await Assert.That(Scalar(connection, "SELECT COUNT(*) FROM _tb_feed;")).IsEqualTo((long)feed.Length);
            await Assert.That(Scalar(connection, "SELECT seq FROM _tb_feed_sequence WHERE id = 1;")).IsEqualTo(counter);
            await Assert.That(Scalar(connection, "SELECT COUNT(*) FROM _tb_shape_chunk;")).IsEqualTo(0L);
            await Assert.That(Scalar(connection, "SELECT COUNT(*) FROM _tb_shape_snapshot;")).IsEqualTo(0L);
            await Assert.That(Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('_tb_shape_chunk', '_tb_shape_snapshot');")).IsEqualTo(2L);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string ShapeChunk = "INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ('h2-feed-seq', 0, 1, X'7B7D', 1791849600000);";
    private const string ShapeSnapshot = "INSERT INTO _tb_shape_snapshot (handle, offset, page, next_id, body, sealed_at) VALUES ('h2-feed-seq', 0, 0, NULL, X'7B7D', 1791849600000);";

    private static async Task<TursoDatabaseStore> StoreAsync(TerraBaseTestServer server, TenantContext tenant) =>
        (TursoDatabaseStore)(await server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;

    private static byte[] Json(int n) => Encoding.UTF8.GetBytes($"{{\"n\":{n}}}");
    private static Hlc FarFuture() => new((ulong)DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() << 16);

    private static async Task PutOneAsync(TursoDatabaseStore store, string id)
    {
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        _ = await transaction.PutAsync(DocumentApi.Collection, id, Json(1), CancellationToken.None);
        _ = await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task<int> TrimAsync(TursoDatabaseStore store, Hlc before)
    {
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        var deleted = await transaction.TrimFeedAsync(before, 1024, CancellationToken.None);
        _ = await transaction.CommitAsync(CancellationToken.None);
        return deleted;
    }

    private static Task<bool> RawWriteAsync(TursoDatabaseStore store, params string[] statements) => store.WithWriterAsync(session =>
    {
        using (var off = session.Connection.CreateCommand())
        {
            off.CommandText = TursoSession.CaptureOff;
            off.ExecuteNonQuery();
        }

        try
        {
            foreach (var sql in statements)
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            using var on = session.Connection.CreateCommand();
            on.CommandText = TursoSession.CaptureOn;
            on.ExecuteNonQuery();
        }

        return true;
    }, "a test writing feed and shape rows uncaptured", CancellationToken.None);

    private static Task<long[]> FeedSeqsAsync(TursoDatabaseStore store) => store.WithWriterAsync(session =>
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = "SELECT seq FROM _tb_feed ORDER BY seq;";
        using var reader = command.ExecuteReader();
        List<long> seqs = [];
        while (reader.Read())
        {
            seqs.Add(reader.GetInt64(0));
        }

        return seqs.ToArray();
    }, "a test reading the feed sequence", CancellationToken.None);

    private static Task<(long Seq, long CommitOffset)[]> FeedRowsAsync(TursoDatabaseStore store) => store.WithWriterAsync(session =>
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = "SELECT seq, commit_offset FROM _tb_feed ORDER BY seq;";
        using var reader = command.ExecuteReader();
        List<(long, long)> rows = [];
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows.ToArray();
    }, "a test reading the feed rows", CancellationToken.None);

    private static Task<long> CounterAsync(TursoDatabaseStore store) =>
        ScalarAsync(store, "SELECT seq FROM _tb_feed_sequence WHERE id = 1;");

    private static Task<long> ScalarAsync(TursoDatabaseStore store, string sql) => store.WithWriterAsync(session =>
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }, "a test reading the feed sequence", CancellationToken.None);

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
