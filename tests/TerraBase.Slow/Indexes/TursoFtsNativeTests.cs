using Turso.Data.Sqlite;

namespace TerraBase.Slow.Indexes;

/// <summary>The pinned custom native core and normal driver opens support FTS without an opt-in.</summary>
public sealed class TursoFtsNativeTests
{
    [Test]
    public async Task Native_fts_interrupts_use_the_sqlite_interrupt_code_and_release_the_callback()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, title TEXT); CREATE INDEX t_fts ON t USING fts(title); INSERT INTO t VALUES (1, 'native search'), (2, 'native search');";
        command.ExecuteNonQuery();
        var calls = 0;
        connection.SetFtsProgressHandler((_, _) => ++calls > 0);
        try
        {
            command.CommandText = "SELECT count(*) FROM t WHERE fts_match(title, 'native');";
            var error = Assert.Throws<SqliteException>(() => command.ExecuteScalar());
            await Assert.That(error.SqliteErrorCode).IsEqualTo(9);
            await Assert.That(calls).IsGreaterThan(0);
        }
        finally
        {
            connection.SetFtsProgressHandler(null);
        }

        command.CommandText = "SELECT count(*) FROM t WHERE fts_match(title, 'native');";
        await Assert.That((long)command.ExecuteScalar()!).IsEqualTo(2);
    }

    [Test]
    public async Task Native_fts_indexes_match_rank_and_reopen()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-native-fts-");
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory.FullName, "fts.db")}";
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, title TEXT); CREATE INDEX t_fts ON t USING fts(title); INSERT INTO t VALUES (1, 'native search'), (2, 'other');";
                command.ExecuteNonQuery();
            }

            using var reopened = new SqliteConnection(connectionString);
            reopened.Open();
            using var query = reopened.CreateCommand();
            query.CommandText = "SELECT id, fts_score(title, $query) AS score FROM t WHERE fts_match(title, $query) ORDER BY score DESC;";
            query.Parameters.AddWithValue("$query", "native");
            using var reader = query.ExecuteReader();
            await Assert.That(reader.Read()).IsTrue();
            await Assert.That(reader.GetInt64(0)).IsEqualTo(1);
            await Assert.That(reader.GetDouble(1)).IsGreaterThan(0);
            await Assert.That(reader.Read()).IsFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Native_fts_postings_follow_update_delete_commit_and_rollback()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        Execute("CREATE TABLE t(id INTEGER PRIMARY KEY, title TEXT); CREATE INDEX t_fts ON t USING fts(title); INSERT INTO t VALUES (1, 'native search');");
        Execute("BEGIN; UPDATE t SET title = 'updated search' WHERE id = 1;");
        await Assert.That(Count("native")).IsEqualTo(0);
        await Assert.That(Count("updated")).IsEqualTo(1);
        Execute("ROLLBACK;");
        await Assert.That(Count("native")).IsEqualTo(1);
        await Assert.That(Count("updated")).IsEqualTo(0);
        Execute("BEGIN; UPDATE t SET title = 'updated search' WHERE id = 1; COMMIT;");
        await Assert.That(Count("native")).IsEqualTo(0);
        await Assert.That(Count("updated")).IsEqualTo(1);
        Execute("BEGIN; DELETE FROM t WHERE id = 1;");
        await Assert.That(Count("updated")).IsEqualTo(0);
        Execute("ROLLBACK;");
        await Assert.That(Count("updated")).IsEqualTo(1);
        Execute("DELETE FROM t WHERE id = 1;");
        await Assert.That(Count("updated")).IsEqualTo(0);
        Execute("BEGIN; INSERT INTO t VALUES (1, 'rolledback insert'); ROLLBACK;");
        await Assert.That(Count("rolledback")).IsEqualTo(0);

        void Execute(string sql)
        {
            command.Parameters.Clear();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        long Count(string term)
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT count(*) FROM t WHERE fts_match(title, $query);";
            command.Parameters.AddWithValue("$query", term);
            return (long)command.ExecuteScalar()!;
        }
    }
}
