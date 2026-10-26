using TerraBase.Engine.Turso;
using TerraBase.Testing;
using Turso;

namespace TerraBase.Fast.Backup;

/// <summary>
/// TursoCopy commits in bounded chunks (P11): one transaction over every row makes Turso read every dirty page the
/// transaction spilled back in at commit, peaking at about 2.2× the file in memory. These tests copy with tiny chunks,
/// so every table crosses many commits, and hold the copy to the same result as one transaction produced.
/// </summary>
public sealed class TursoCopyChunkingTests
{
    [Test]
    public async Task Chunked_copy_holds_every_row_rowid_index_and_bookkeeping()
    {
        var directory = Directory.CreateTempSubdirectory("turso-copy-chunk-");
        try
        {
            var source = Path.Combine(directory.FullName, "source.db");
            TestMasterKey.Create(source);
            using (var connection = new TursoConnection(TestMasterKey.ConnectionString(source, Turso.Data.Sqlite.SqliteOpenMode.ReadWriteCreate)))
            {
                connection.Open();
                Execute(connection, "CREATE TABLE events (id INTEGER PRIMARY KEY, acct TEXT, note TEXT, amount Cents INTEGER);");
                Execute(connection, "CREATE TABLE markers (name TEXT, payload BLOB);");
                Execute(connection, "CREATE TABLE _tb_peers (peer TEXT, acked INTEGER);");
                Execute(connection, "CREATE TABLE commit_offset (incarnation TEXT, ordinal INTEGER);");
                Execute(connection, "CREATE INDEX events_acct ON events(acct);");
                Execute(connection, "INSERT INTO commit_offset VALUES ('source-incarnation', 41);");
                Execute(connection, "INSERT INTO _tb_peers VALUES ('peer-a', 907), ('peer-b', 12);");
                using var events = connection.CreateCommand();
                {
                    events.CommandText = "INSERT INTO events VALUES ($id, $acct, $note, $amount);";
                    for (var i = 1; i <= 50; i++)
                    {
                        events.Parameters.Clear();
                        events.Parameters.Add(new TursoParameter("$id", (long)i * 3));
                        events.Parameters.Add(new TursoParameter("$acct", "acct-" + i % 7));
                        events.Parameters.Add(new TursoParameter("$note", i % 5 == 0 ? DBNull.Value : "\u00e9\ud83c\udf0d" + new string('n', 40 + i)));
                        events.Parameters.Add(new TursoParameter("$amount", (long)i * 100));
                        events.ExecuteNonQuery();
                    }
                }

                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO markers VALUES ($name, $payload);";
                for (var i = 0; i < 9; i++)
                {
                    insert.Parameters.Clear();
                    insert.Parameters.Add(new TursoParameter("$name", "marker-" + i));
                    insert.Parameters.Add(new TursoParameter("$payload", new byte[] { (byte)i, 1, 2, 3 }));
                    insert.ExecuteNonQuery();
                }

                Execute(connection, "PRAGMA user_version = 77;");
            }

            // Two rows per commit: every table's copy spans many transactions.
            var copy = Path.Combine(directory.FullName, "copy.db");
            TestMasterKey.Create(copy);
            using var reader = new TursoConnection(TestMasterKey.ConnectionString(source));
            reader.Open();
            Execute(reader, "BEGIN;");
            // Rewrite derives the target string from an existing source: the copy must create its scratch file even with ReadWrite.
            TursoCopy.Write(reader, TestMasterKey.ConnectionString(copy), chunkRows: 2, chunkBytes: 64, CancellationToken.None);

            using var target = new TursoConnection(TestMasterKey.ConnectionString(copy));
            target.Open();
            await Assert.That(Scalar(target, "SELECT count(*) FROM events;")).IsEqualTo(50L);
            await Assert.That(Scalar(target, "SELECT count(*) FROM markers;")).IsEqualTo(9L);
            // Rowids are kept exactly, including the holes the source's ids left.
            await Assert.That(Scalar(target, "SELECT min(id) FROM events;")).IsEqualTo(3L);
            await Assert.That(Scalar(target, "SELECT max(id) FROM events;")).IsEqualTo(150L);
            await Assert.That(Scalar(target, "SELECT count(*) FROM events WHERE id % 3 != 0;")).IsEqualTo(0L);
            await Assert.That(Scalar(target, "SELECT note FROM events WHERE id = 15;")).IsEqualTo(DBNull.Value);
            await Assert.That(Scalar(target, "SELECT count(*) FROM events WHERE note IS NULL;")).IsEqualTo(10L);
            await Assert.That(Scalar(target, "SELECT note FROM events WHERE id = 3;")).IsEqualTo("\u00e9\ud83c\udf0d" + new string('n', 41));
            await Assert.That(Scalar(target, "SELECT hex(payload) FROM markers WHERE name = 'marker-4';")).IsEqualTo("04010203");
            // The index came with the rows: i % 7 == 0 over 1..50 is seven rows.
            await Assert.That(Scalar(target, "SELECT count(*) FROM events WHERE acct = 'acct-0';")).IsEqualTo(7L);
            await Assert.That(Scalar(target, "SELECT count(*) FROM pragma_index_list('events');")).IsEqualTo(1L);
            // Bookkeeping: peers' acks reset, a fresh incarnation, user_version kept.
            await Assert.That(Scalar(target, "SELECT count(*) FROM _tb_peers WHERE acked != 0;")).IsEqualTo(0L);
            await Assert.That(Scalar(target, "SELECT incarnation FROM commit_offset;")).IsNotEqualTo("source-incarnation");
            await Assert.That(Scalar(target, "SELECT ordinal FROM commit_offset;")).IsEqualTo(41L);
            await Assert.That(Scalar(target, "PRAGMA user_version;")).IsEqualTo(77L);
            await Assert.That(Scalar(target, "PRAGMA integrity_check;")).IsEqualTo("ok");
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task Chunked_copy_reports_cancellation_between_rows()
    {
        var directory = Directory.CreateTempSubdirectory("turso-copy-cancel-");
        try
        {
            var source = Path.Combine(directory.FullName, "source.db");
            TestMasterKey.Create(source);
            using (var connection = new TursoConnection(TestMasterKey.ConnectionString(source, Turso.Data.Sqlite.SqliteOpenMode.ReadWriteCreate)))
            {
                connection.Open();
                Execute(connection, "CREATE TABLE events (id INTEGER PRIMARY KEY, note TEXT);");
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO events VALUES ($id, $note);";
                for (var i = 1; i <= 10; i++)
                {
                    insert.Parameters.Clear();
                    insert.Parameters.Add(new TursoParameter("$id", (long)i));
                    insert.Parameters.Add(new TursoParameter("$note", "note-" + i));
                    insert.ExecuteNonQuery();
                }
            }

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            using (var connection = new TursoConnection(TestMasterKey.ConnectionString(source)))
            {
                connection.Open();
                Execute(connection, "BEGIN;");
                var copy = Path.Combine(directory.FullName, "copy.db");
                TestMasterKey.Create(copy);
                await Assert.That(() => TursoCopy.Write(connection, TestMasterKey.ConnectionString(copy, Turso.Data.Sqlite.SqliteOpenMode.ReadWriteCreate), chunkRows: 2, chunkBytes: 64, cancelled.Token))
                    .Throws<OperationCanceledException>();
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static void Execute(TursoConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(TursoConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
