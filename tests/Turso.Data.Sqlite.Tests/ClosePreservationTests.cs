using System.Data;

namespace Turso.Data.Sqlite.Tests;

/// <summary>Refused local closes preserve both files and release native ownership; healthy closes still checkpoint.</summary>
public sealed class ClosePreservationTests
{
    /// <summary>Healthy close checkpoints; a refusal published after open preserves the files and releases their handles.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClosingAFinalizedWriterUsesTheCurrentCopyRefusal(bool refused)
    {
        var directory = Directory.CreateTempSubdirectory("turso-close-").FullName;
        try
        {
            var path = Path.Combine(directory, "t.db");
            string? refusal = null;
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False")
            {
                CloseRefusal = () => refusal,
            };
            OpenWithoutRetainingTheSchemaConnection(connection, path);
            CreateNonemptyWal(connection);
            await AssertCommittedRowIsVisibleToAnotherConnection(path);
            var main = File.ReadAllBytes(path);
            var wal = File.ReadAllBytes(path + "-wal");
            await Assert.That(wal.Length).IsGreaterThan(32);
            // Opened healthy: close must read the live refusal, not the value at open.
            refusal = refused ? "this copy is faulted" : null;
            connection.Close();
            await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed);
            if (refused)
            {
                await Assert.That(File.ReadAllBytes(path).SequenceEqual(main)).IsTrue();
                await Assert.That(File.ReadAllBytes(path + "-wal").SequenceEqual(wal)).IsTrue();
            }
            else
            {
                await Assert.That(File.ReadAllBytes(path).SequenceEqual(main)).IsFalse();
                await Assert.That(File.ReadAllBytes(path + "-wal").Length).IsEqualTo(0);
            }
            if (OperatingSystem.IsLinux())
            {
                await Assert.That(OpenHandles(path)).IsEqualTo(0);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void OpenWithoutRetainingTheSchemaConnection(SqliteConnection connection, string path)
    {
        // Native 0.8.2 retains the first opener's schema connection; release it before testing the last close.
        using var schemaOwner = new SqliteConnection($"Data Source={path};Pooling=False");
        schemaOwner.Open();
        connection.Open();
    }

    private static async Task AssertCommittedRowIsVisibleToAnotherConnection(string path)
    {
        using var observer = new SqliteConnection($"Data Source={path};Pooling=False");
        observer.Open();
        using var command = observer.CreateCommand();
        command.CommandText = "SELECT id FROM t;";
        using var rows = command.ExecuteReader();
        await Assert.That(rows.Read()).IsTrue();
        await Assert.That(rows.GetInt64(0)).IsEqualTo(1);
        await Assert.That(rows.Read()).IsFalse();
    }

    private static void CreateNonemptyWal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY);";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO t VALUES (1);";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static int OpenHandles(string path)
    {
        var count = 0;
        foreach (var descriptor in Directory.EnumerateFiles("/proc/self/fd"))
        {
            try
            {
                var target = new FileInfo(descriptor).LinkTarget;
                if (target == path || target == path + "-wal")
                {
                    count++;
                }
            }
            catch (IOException)
            {
                // A descriptor owned by a different parallel test closed between enumeration and readlink.
            }
        }
        return count;
    }

}
