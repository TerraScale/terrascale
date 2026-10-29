using System.Security.Cryptography;
using System.Text;
using Turso.Raw.Public;

namespace Turso.Data.Sqlite.Tests;

/// <summary>The native WAL revision contract through the driver: manual WAL, frame metadata and snapshot page reads.</summary>
public sealed class WalRevisionContractTests
{
    /// <summary>Manual WAL keeps every frame and the database file untouched until an explicit TRUNCATE; snapshot reads decrypt.</summary>
    [Test]
    public async Task ManualWalKeepsFramesUntilAnExplicitTruncate()
    {
        var directory = Directory.CreateTempSubdirectory("turso-wal-").FullName;
        try
        {
            var path = Path.Combine(directory, "t.db");
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                EncryptionCipher = "aegis256",
                EncryptionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Pooling = false,
            };
            ulong written;
            using (var writer = Open(builder.ConnectionString))
            using (var reader = Open(builder.ConnectionString))
            {
                Execute(writer, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1, 'marker-before');");
                var first = writer.GetWalState();
                await Assert.That(first.MaxFrame).IsGreaterThan(0UL);
                var page = new byte[reader.PageSize];
                await Assert.That(() => reader.TryReadSnapshotPage(1, page)).Throws<TursoException>();

                Execute(reader, "BEGIN; SELECT count(*) FROM sqlite_schema;");
                var leaf = FindPage(reader, first.MaxFrame, "marker-before");
                await Assert.That(leaf).IsGreaterThan(1U);
                Execute(writer, "UPDATE t SET v = 'marker-after!' WHERE id = 1;");
                await Assert.That(reader.TryReadSnapshotPage(leaf, page)).IsTrue();
                await Assert.That(Contains(page, "marker-before")).IsTrue();
                Execute(reader, "ROLLBACK; BEGIN; SELECT count(*) FROM sqlite_schema;");
                await Assert.That(reader.TryReadSnapshotPage(leaf, page)).IsTrue();
                await Assert.That(Contains(page, "marker-after!")).IsTrue();
                Execute(reader, "ROLLBACK;");

                Execute(writer, "BEGIN IMMEDIATE; INSERT INTO t VALUES (0, 'uncommitted');");
                await Assert.That(writer.GetWalState).Throws<TursoException>();
                Execute(writer, "ROLLBACK;");

                var stamp = Stamp(path);
                for (var i = 0; i < 1200; i++)
                {
                    Execute(writer, $"INSERT INTO t VALUES ({i + 2}, 'row-{i}');");
                }

                var state = writer.GetWalState();
                await Assert.That(state.MaxFrame).IsGreaterThan(first.MaxFrame + 1200);
                await Assert.That(state.CheckpointSequence).IsEqualTo(first.CheckpointSequence);
                await Assert.That(Stamp(path)).IsEqualTo(stamp);
                written = state.MaxFrame;
            }

            await Assert.That(new FileInfo(path + "-wal").Length).IsGreaterThan(0L);
            using (var writer = Open(builder.ConnectionString))
            using (var reader = Open(builder.ConnectionString))
            {
                await Assert.That(writer.GetWalState().MaxFrame).IsEqualTo(written);
                Execute(writer, "PRAGMA wal_checkpoint(TRUNCATE);");
                await Assert.That(writer.GetWalState().MaxFrame).IsEqualTo(0UL);
                Execute(reader, "BEGIN; SELECT count(*) FROM sqlite_schema;");
                var page = new byte[reader.PageSize];
                var marked = 0;
                for (uint number = 1; number <= (long)Scalar(reader, "PRAGMA page_count;"); number++)
                {
                    await Assert.That(reader.TryReadSnapshotPage(number, page)).IsTrue();
                    marked += Contains(page, "marker-after!") ? 1 : 0;
                }

                Execute(reader, "ROLLBACK;");
                await Assert.That(marked).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Overlapping buffers and frames past the end of the WAL are refused.</summary>
    [Test]
    public async Task FramePageRangesAndBuffersAreChecked()
    {
        using var database = new TemporaryDatabase();
        database.Connection.UseManualWal();
        var frames = database.Connection.GetWalState().MaxFrame;
        var buffer = new uint[4];
        await Assert.That(() => database.Connection.ReadWalFramePages(1, buffer.AsSpan(0, 2), buffer.AsSpan(0, 2))).Throws<ArgumentException>();
        await Assert.That(() => database.Connection.ReadWalFramePages(1, buffer.AsSpan(0, 2), buffer.AsSpan(1, 2))).Throws<ArgumentException>();
        await Assert.That(() => database.Connection.ReadWalFramePages(frames + 1, buffer.AsSpan(0, 1), buffer.AsSpan(2, 1))).Throws<TursoException>();
    }

    private static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        connection.UseManualWal();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() ?? throw new InvalidOperationException("no scalar result");
    }

    private static (long Length, DateTime Written) Stamp(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static uint FindPage(SqliteConnection reader, ulong frames, string marker)
    {
        var pages = new uint[(int)frames];
        var sizes = new uint[(int)frames];
        reader.ReadWalFramePages(1, pages, sizes);
        if (sizes[^1] == 0)
        {
            throw new InvalidOperationException("the last frame is not a commit frame");
        }

        var page = new byte[reader.PageSize];
        foreach (var number in pages.Distinct())
        {
            if (reader.TryReadSnapshotPage(number, page) && Contains(page, marker))
            {
                return number;
            }
        }

        return 0;
    }

    private static bool Contains(byte[] page, string marker) =>
        page.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)) >= 0;
}
