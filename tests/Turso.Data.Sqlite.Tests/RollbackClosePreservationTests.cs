namespace Turso.Data.Sqlite.Tests;

/// <summary>Terminal refused cleanup also preserves files when provider rollback never reaches the native transaction.</summary>
public sealed class RollbackClosePreservationTests
{
    /// <summary>A failed rollback leaves native work uncommitted; refused teardown releases it without checkpoint or commit.</summary>
    [Test]
    public async Task AFailedRollbackCannotCommitOrCheckpointTheRefusedCopy()
    {
        var directory = Directory.CreateTempSubdirectory("turso-rollback-close-").FullName;
        try
        {
            var path = Path.Combine(directory, "t.db");
            using var connection = new FailingRollbackConnection($"Data Source={path};Pooling=False");
            using (var schemaOwner = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                schemaOwner.Open();
                connection.Open();
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1);";
                command.ExecuteNonQuery();
            }

            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO t VALUES (2);";
                command.ExecuteNonQuery();
            }

            // All native statements ended; retain the worst case where failed cleanup leaves the transaction active.
            var main = File.ReadAllBytes(path);
            var wal = File.ReadAllBytes(path + "-wal");
            await Assert.That(wal.Length).IsGreaterThan(0);
            connection.RefuseAndFailRollback();
            _ = Assert.Throws<SqliteException>(transaction.Dispose);
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "SELECT count(*) FROM t;";
                await Assert.That((long)probe.ExecuteScalar()!).IsEqualTo(2)
                    .Because("the provider marked completion but the native uncommitted write still exists");
                probe.CommandText = "BEGIN;";
                _ = Assert.Throws<SqliteException>(() => probe.ExecuteNonQuery());
            }

            connection.Close();
            await Assert.That(File.ReadAllBytes(path).SequenceEqual(main)).IsTrue();
            await Assert.That(File.ReadAllBytes(path + "-wal").SequenceEqual(wal)).IsTrue();
            if (OperatingSystem.IsLinux())
            {
                var handles = Directory.EnumerateFiles("/proc/self/fd").Count(descriptor =>
                {
                    try
                    {
                        var target = new FileInfo(descriptor).LinkTarget;
                        return target == path || target == path + "-wal";
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                });
                await Assert.That(handles).IsEqualTo(0);
            }

            using var reopened = new SqliteConnection($"Data Source={path};Pooling=False");
            reopened.Open();
            using var read = reopened.CreateCommand();
            read.CommandText = "SELECT id FROM t;";
            using var rows = read.ExecuteReader();
            await Assert.That(rows.Read()).IsTrue();
            await Assert.That(rows.GetInt64(0)).IsEqualTo(1);
            await Assert.That(rows.Read()).IsFalse().Because("terminal cleanup must never commit the abandoned write");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FailingRollbackConnection(string connectionString) : SqliteConnection(connectionString)
    {
        private bool _refused;

        public override void Open()
        {
            CloseRefusal = () => _refused ? "the copy is refused" : null;
            base.Open();
        }

        internal void RefuseAndFailRollback() => _refused = true;

        public override SqliteCommand CreateCommand() => new RollbackCommand(this);

        private sealed class RollbackCommand(FailingRollbackConnection owner) : SqliteCommand(owner)
        {
            public override int ExecuteNonQuery()
            {
                if (((FailingRollbackConnection)Connection!)._refused && CommandText == "ROLLBACK;")
                {
                    throw new SqliteException("rollback failed before native execution", 10);
                }

                return base.ExecuteNonQuery();
            }
        }
    }
}
