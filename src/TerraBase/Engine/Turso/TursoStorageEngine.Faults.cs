using System.Security.Cryptography;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

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
                using var connection = new SqliteConnection(connectionString);
                TursoSession.OpenExisting(connection, path);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode = WAL;";
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA integrity_check;";
                if (command.ExecuteScalar() as string != "ok")
                {
                    return "the recovered database failed its integrity check";
                }

                command.CommandText = TursoStatements.Incarnation;
                var recovered = command.ExecuteScalar() as string;
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
