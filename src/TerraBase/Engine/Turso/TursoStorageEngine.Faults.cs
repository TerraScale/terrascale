using System.Diagnostics;
using System.Security.Cryptography;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    internal bool FileExists(TenantContext database) => File.Exists(Locate(database).Path);

    internal async Task<string?> RecoverCopyAsync(TenantContext database, Ulid incarnation, Ulid replacement, CancellationToken cancellationToken)
    {
        var path = Locate(database).Path;
        try
        {
            var connectionString = await Encryption.ExistingAsync(path, Io, cancellationToken).ConfigureAwait(false);
            return await Io.RunAsync(() =>
            {
                var started = Stopwatch.GetTimestamp();
                using var connection = new SqliteConnection(connectionString);
                TursoSession.OpenExisting(connection, path);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode = WAL;";
                command.ExecuteNonQuery();
                command.CommandText = TursoStatements.Incarnation;
                var recovered = command.ExecuteScalar() as string;
                var replayMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                started = Stopwatch.GetTimestamp();
                command.CommandText = "PRAGMA integrity_check;";
                if (command.ExecuteScalar() as string != "ok")
                {
                    return "the recovered database failed its integrity check";
                }

                _logger.ZLogInformation($"Database restart proof: replay {replayMs:@ReplayMs} ms, integrity {Stopwatch.GetElapsedTime(started).TotalMilliseconds:@IntegrityMs} ms, file {new FileInfo(path).Length:@Bytes} bytes");
                return recovered == incarnation.ToString() || recovered == replacement.ToString()
                    ? null : "the recovered database incarnation does not match its write intent";
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SqliteException or IOException or InvalidDataException or CryptographicException or UnauthorizedAccessException)
        {
            return "the database or its log could not be recovered";
        }
    }
}
