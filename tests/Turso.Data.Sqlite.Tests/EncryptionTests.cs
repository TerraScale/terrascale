using System.Security.Cryptography;
using System.Text;
using Turso.Raw.Public;

namespace Turso.Data.Sqlite.Tests;

/// <summary>Local encryption reaches native opens without sharing handles across keys.</summary>
public sealed class EncryptionTests
{
    /// <summary>Builder aliases preserve both encryption options.</summary>
    [Test]
    public async Task BuilderPreservesEncryptionOptions()
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var builder = new SqliteConnectionStringBuilder
        {
            EncryptionCipher = "aegis256",
            EncryptionKey = key,
        };
        var parsed = new SqliteConnectionStringBuilder(builder.ConnectionString);
        await Assert.That(parsed["EncryptionCipher"]).IsEqualTo("aegis256");
        await Assert.That(parsed["EncryptionKey"]).IsEqualTo(key);
    }

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

    /// <summary>Raw backup writers and facade readers use the same local encryption configuration.</summary>
    [Test]
    public async Task RawConnectionCanCreateEncryptedBackup()
    {
        var directory = Directory.CreateTempSubdirectory("turso-encrypted-copy-").FullName;
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "backup.db"),
                EncryptionCipher = "aegis256",
                EncryptionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Pooling = false,
            };
            using (var raw = new Turso.TursoConnection(builder.ConnectionString))
            {
                raw.Open();
                using var write = raw.CreateCommand();
                write.CommandText = "CREATE TABLE secret(value TEXT);";
                write.ExecuteNonQuery();
                write.CommandText = "INSERT INTO secret VALUES ('backup-payload');";
                write.ExecuteNonQuery();
            }
            await Assert.That(ReadValue(builder.ConnectionString)).IsEqualTo("backup-payload");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Authenticated-page tamper remains native damage through both driver surfaces.</summary>
    [Test]
    public async Task EncryptedPageTamperPreservesDamageClassification()
    {
        var directory = Directory.CreateTempSubdirectory("turso-encrypted-tamper-").FullName;
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "tamper.db"),
                EncryptionCipher = "aegis256",
                EncryptionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Pooling = false,
            };
            long offset;
            using (var connection = new SqliteConnection(builder.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE secret(value TEXT); INSERT INTO secret VALUES ('authenticated');";
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA page_size;";
                var pageSize = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                command.CommandText = "SELECT rootpage FROM sqlite_schema WHERE name='secret';";
                offset = (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) - 1) * pageSize;
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                command.ExecuteNonQuery();
            }

            using (var file = new FileStream(builder.DataSource, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                file.Position = offset;
                var original = file.ReadByte();
                await Assert.That(original).IsGreaterThanOrEqualTo(0);
                file.Position = offset;
                file.WriteByte((byte)(original ^ 0x80));
                file.Flush(flushToDisk: true);
            }

            using (var raw = new Turso.TursoConnection(builder.ConnectionString))
            {
                var nativeError = Assert.Throws<TursoNativeException>(() =>
                {
                    raw.Open();
                    using var read = raw.CreateCommand();
                    read.CommandText = "SELECT value FROM secret;";
                    read.ExecuteScalar();
                });
                await Assert.That(nativeError!.StatusCode).IsEqualTo(127U);
                await Assert.That(nativeError.IsPageAuthenticationFailure).IsTrue();
            }
            var facadeError = Assert.Throws<SqliteException>(() => ReadValue(builder.ConnectionString));
            await Assert.That(facadeError!.SqliteErrorCode).IsEqualTo(11);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>SQL rejection text cannot impersonate a damaged encrypted page.</summary>
    [Test]
    public async Task SqlRaiseCannotImpersonatePageAuthenticationFailure()
    {
        var directory = Directory.CreateTempSubdirectory("turso-encrypted-raise-").FullName;
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "healthy.db"),
                EncryptionCipher = "aegis256",
                EncryptionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Pooling = false,
            };
            using (var raw = new Turso.TursoConnection(builder.ConnectionString))
            {
                raw.Open();
                using var command = raw.CreateCommand();
                command.CommandText = "CREATE TABLE secret(value TEXT);";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO secret VALUES ('healthy');";
                command.ExecuteNonQuery();
                // Raw prepare retains the complete trigger body, including its internal semicolon.
                command.CommandText = "CREATE TRIGGER reject_insert BEFORE INSERT ON secret BEGIN SELECT RAISE(ABORT, 'Decryption failed for page=2'); END;";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO secret VALUES ('rejected');";
                var nativeError = Assert.Throws<TursoNativeException>(() => command.ExecuteNonQuery());
                await Assert.That(nativeError!.StatusCode).IsEqualTo(127U);
                await Assert.That(nativeError.IsPageAuthenticationFailure).IsFalse();
            }
            using (var connection = new SqliteConnection(builder.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO secret VALUES ('rejected');";
                var sqlError = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
                await Assert.That(sqlError!.SqliteErrorCode).IsEqualTo(1);
            }
            await Assert.That(ReadValue(builder.ConnectionString)).IsEqualTo("healthy");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Incomplete or malformed encryption never silently creates a plaintext database.</summary>
    [Test]
    [Arguments("aegis256", "")]
    [Arguments("", "invalid-key-marker")]
    [Arguments("aegis256", "invalid-key-marker")]
    public async Task InvalidEncryptionFailsWithoutExposingKey(string cipher, string key)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            EncryptionCipher = cipher,
            EncryptionKey = key,
        };
        Exception? error = null;
        try
        {
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        await Assert.That(error).IsNotNull();
        if (key.Length > 0)
        {
            await Assert.That(error!.ToString().Contains(key, StringComparison.Ordinal)).IsFalse();
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
