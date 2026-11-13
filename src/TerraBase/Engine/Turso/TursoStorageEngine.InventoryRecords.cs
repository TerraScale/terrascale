using System.Security.Cryptography;
using System.Text;
using TerraBase.Features.Backup;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    private long InventoryCatalog(bool startup, Dictionary<string, long> counts,
        Dictionary<string, long> missing, CancellationToken cancellationToken)
    {
        long images = 0;
        foreach (var path in new[] { LocateSystem(), Path.Combine(_dataDirectory, IntentFile) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                continue;
            }
            var key = Encryption.Keyring.Unwrap(DatabaseKeys.ReadWrapped(path));
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    EncryptionCipher = "aegis256",
                    EncryptionKey = Convert.ToHexString(key),
                }.ToString());
                TursoSession.OpenExisting(connection, path);
                using var transaction = connection.BeginTransaction(deferred: true);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                using var cancel = cancellationToken.Register(command.Cancel);
                command.CommandText = """
                    SELECT body FROM _tb_records
                    WHERE kind GLOB 'backup_destination_history/*' OR kind GLOB 'projects/*/backup_destination'
                    """;
                using (var rows = command.ExecuteReader())
                {
                    while (rows.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (rows.GetValue(0) is string body)
                        {
                            CountCredential(Encoding.UTF8.GetBytes(body), startup, counts, missing);
                        }
                        else
                        {
                            Malformed(counts);
                        }
                        images++;
                    }
                }
                command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';";
                if (command.ExecuteScalar() is null)
                {
                    continue;
                }
                command.CommandText = "SELECT before, after FROM turso_cdc WHERE table_name = '_tb_records';";
                using var changes = command.ExecuteReader();
                while (changes.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var index = 0; index < 2; index++)
                    {
                        if (changes.IsDBNull(index))
                        {
                            continue;
                        }
                        images++;
                        if (changes.GetValue(index) is byte[] image)
                        {
                            CountRetained(image, startup, counts, missing);
                        }
                        else
                        {
                            Malformed(counts);
                        }
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        return images;
    }

    private void CountRetained(ReadOnlySpan<byte> image, bool startup,
        Dictionary<string, long> counts, Dictionary<string, long> missing)
    {
        try
        {
            var values = SqliteRecord.Decode(image);
            if (values is not [string kind, string, long, string body])
            {
                throw new InvalidDataException("retained credential image is malformed");
            }
            if (TursoStorageTransaction.CredentialKind(kind))
            {
                CountCredential(Encoding.UTF8.GetBytes(body), startup, counts, missing);
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        {
            Malformed(counts);
        }
    }
}
