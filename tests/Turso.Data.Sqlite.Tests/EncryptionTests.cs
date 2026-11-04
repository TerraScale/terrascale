using System.Security.Cryptography;
using System.Text;

namespace Turso.Data.Sqlite.Tests;

/// <summary>Local encryption reaches native opens without sharing handles across keys.</summary>
public sealed class EncryptionTests
{
    /// <summary>Database and live WAL copies retain encryption and reopen with the original key only.</summary>
    [Test]
    public async Task EncryptedDatabaseAndWalCopiesRequireTheKey()
    {
        var directory = Directory.CreateTempSubdirectory("turso-encryption-").FullName;
        try
        {
            var path = Path.Combine(directory, "source.db");
            var copy = Path.Combine(directory, "copy.db");
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                EncryptionCipher = "aegis256",
                EncryptionKey = key,
                // Exercise the default Pooling=true: encrypted opens must bypass the filename-only pool.
            };
            using (var connection = new SqliteConnection(builder.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE secret(value TEXT); INSERT INTO secret VALUES ('private-payload-marker');";
                command.ExecuteNonQuery();
                await Assert.That(File.Exists(path + "-wal")).IsTrue();
                foreach (var file in (string[])[path, path + "-wal"])
                {
                    var bytes = File.ReadAllBytes(file);
                    await Assert.That(Encoding.Latin1.GetString(bytes).Contains("private-payload-marker", StringComparison.Ordinal)).IsFalse();
                    File.Copy(file, file.Replace(path, copy, StringComparison.Ordinal));
                }

                var wrong = new SqliteConnectionStringBuilder(builder.ConnectionString)
                {
                    EncryptionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                };
                var error = Assert.Throws<SqliteException>(() => ReadValue(wrong.ConnectionString));
                await Assert.That(error.ToString().Contains(wrong.EncryptionKey, StringComparison.Ordinal)).IsFalse();
            }

            await Assert.That(ReadValue(builder.ConnectionString)).IsEqualTo("private-payload-marker");
            using (var raw = new Turso.TursoConnection(builder.ConnectionString))
            {
                raw.Open();
                using var read = raw.CreateCommand();
                read.CommandText = "SELECT value FROM secret;";
                await Assert.That(read.ExecuteScalar() as string).IsEqualTo("private-payload-marker");
            }
            builder.DataSource = copy;
            await Assert.That(ReadValue(builder.ConnectionString)).IsEqualTo("private-payload-marker");
            var missingKey = new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false };
            Assert.Throws<SqliteException>(() => ReadValue(missingKey.ConnectionString));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string? ReadValue(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM secret;";
        return command.ExecuteScalar() as string;
    }
}
