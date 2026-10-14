namespace Turso.Data.Sqlite.Tests;

/// <summary>
/// A local error keeps the SQLite code of Turso's native status when it reports damage: SQLITE_CORRUPT (11) for a
/// corrupt page and SQLITE_NOTADB (26) for a file that is not a database. Every other error stays SQLITE_ERROR (1).
/// </summary>
public sealed class DamageErrorCodeTests
{
    /// <summary>A read or a write that meets a corrupt page fails with SQLITE_CORRUPT.</summary>
    [Test]
    [Arguments("SELECT count(*) FROM t;")]
    [Arguments("INSERT INTO t VALUES ('b');")]
    public async Task AStatementOnACorruptPageFailsWithSqliteCorrupt(string sql)
    {
        var directory = Directory.CreateTempSubdirectory("turso-driver-").FullName;
        try
        {
            var path = Path.Combine(directory, "t.db");
            var connectionString = $"Data Source={path};Pooling=False";
            long pageSize;
            using (var create = new SqliteConnection(connectionString))
            {
                create.Open();
                using var command = create.CreateCommand();
                command.CommandText = "CREATE TABLE t (id TEXT NOT NULL); INSERT INTO t VALUES ('a');";
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA page_size;";
                pageSize = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            // Page 2 is t's root page: 0xFF is no b-tree page type.
            await Assert.That(new FileInfo(path).Length).IsGreaterThanOrEqualTo(2 * pageSize);
            using (var file = File.OpenWrite(path))
            {
                file.Position = pageSize;
                file.WriteByte(0xFF);
            }

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var statement = connection.CreateCommand();
            statement.CommandText = sql;

            var error = Assert.Throws<SqliteException>(() => statement.ExecuteNonQuery());

            await Assert.That(error.SqliteErrorCode).IsEqualTo(11);
            await Assert.That(error.SqliteExtendedErrorCode).IsEqualTo(11);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A file that is not a database fails with SQLITE_NOTADB, at open or at its first statement.</summary>
    [Test]
    public async Task AFileThatIsNotADatabaseFailsWithSqliteNotADatabase()
    {
        var directory = Directory.CreateTempSubdirectory("turso-driver-").FullName;
        try
        {
            var path = Path.Combine(directory, "t.db");
            File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)'B', 4096)]);

            var error = Assert.Throws<SqliteException>(() =>
            {
                using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
                connection.Open();
                using var statement = connection.CreateCommand();
                statement.CommandText = "SELECT count(*) FROM sqlite_schema;";
                statement.ExecuteNonQuery();
            });

            await Assert.That(error.SqliteErrorCode).IsEqualTo(26);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Syntax errors and constraint violations are no damage: they stay SQLITE_ERROR.</summary>
    [Test]
    [Arguments("SELEC 1;")]
    [Arguments("INSERT INTO t VALUES ('a');")]
    public async Task OtherErrorsStaySqliteError(string sql)
    {
        using var database = new TemporaryDatabase();
        using var statement = database.Connection.CreateCommand();
        statement.CommandText = sql;

        var error = Assert.Throws<SqliteException>(() => statement.ExecuteNonQuery());

        await Assert.That(error.SqliteErrorCode).IsEqualTo(1);
    }
}
