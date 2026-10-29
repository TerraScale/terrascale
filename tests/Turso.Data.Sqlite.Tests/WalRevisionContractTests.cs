using System.Security.Cryptography;
using System.Text;

namespace Turso.Data.Sqlite.Tests;

public sealed class WalRevisionContractTests
{
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
            using (var connection = Open(builder.ConnectionString))
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1, 'marker-before');");
                var first = connection.GetWalState();
                await Assert.That(first.MaxFrame).IsGreaterThan(0UL);
                var leaf = FindPage(connection, first.MaxFrame, "marker-before");
                await Assert.That(leaf).IsGreaterThan(1U);

                Execute(connection, "UPDATE t SET v = 'marker-after!' WHERE id = 1;");
                var page = new byte[connection.PageSize];
                await Assert.That(connection.TryReadPage(leaf, first.MaxFrame, page)).IsTrue();
                await Assert.That(Contains(page, "marker-before")).IsTrue();
                await Assert.That(connection.TryReadPage(leaf, connection.GetWalState().MaxFrame, page)).IsTrue();
                await Assert.That(Contains(page, "marker-after!")).IsTrue();

                for (var i = 0; i < 1200; i++)
                {
                    Execute(connection, $"INSERT INTO t VALUES ({i + 2}, 'row-{i}');");
                }

                var state = connection.GetWalState();
                await Assert.That(state.MaxFrame).IsGreaterThan(1200UL);
                await Assert.That(state.CheckpointSequence).IsEqualTo(first.CheckpointSequence);
                written = state.MaxFrame;
            }

            await Assert.That(new FileInfo(path + "-wal").Length).IsGreaterThan(0L);
            using (var connection = Open(builder.ConnectionString))
            {
                await Assert.That(connection.GetWalState().MaxFrame).IsEqualTo(written);
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                await Assert.That(connection.GetWalState().MaxFrame).IsEqualTo(0UL);
                var page = new byte[connection.PageSize];
                var marked = 0;
                for (uint number = 1; number <= (long)Scalar(connection, "PRAGMA page_count;"); number++)
                {
                    await Assert.That(connection.TryReadPage(number, 0, page)).IsTrue();
                    marked += Contains(page, "marker-after!") ? 1 : 0;
                }

                await Assert.That(marked).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task FramePageBuffersMustNotOverlap()
    {
        using var database = new TemporaryDatabase();
        database.Connection.UseManualWal();
        var buffer = new uint[4];
        await Assert.That(() => database.Connection.ReadWalFramePages(1, buffer.AsSpan(0, 2), buffer.AsSpan(0, 2))).Throws<ArgumentException>();
        await Assert.That(() => database.Connection.ReadWalFramePages(1, buffer.AsSpan(0, 2), buffer.AsSpan(1, 2))).Throws<ArgumentException>();
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

    private static uint FindPage(SqliteConnection connection, ulong watermark, string marker)
    {
        var pages = new uint[(int)watermark];
        var sizes = new uint[(int)watermark];
        connection.ReadWalFramePages(1, pages, sizes);
        if (sizes[^1] == 0)
        {
            throw new InvalidOperationException("the last frame is not a commit frame");
        }

        var page = new byte[connection.PageSize];
        foreach (var number in pages.Distinct())
        {
            if (connection.TryReadPage(number, watermark, page) && Contains(page, marker))
            {
                return number;
            }
        }

        return 0;
    }

    private static bool Contains(byte[] page, string marker) =>
        page.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)) >= 0;
}
