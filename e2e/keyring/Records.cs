using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Configuration;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using Turso.Data.Sqlite;
using MasterKeyring = TerraBase.Features.Backup.Keyring;

namespace Journey.Keyring;

internal static class Records
{
    internal static async Task RunAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        var keyring = services.GetRequiredService<IKeyring>();
        if (args[0] == "credentials")
        {
            var catalog = services.GetRequiredService<SystemCatalog>();
            var kinds = await catalog.ReadAsync(transaction => transaction.ListKindsAsync(null, 100, cancellationToken),
                static error => throw new IOException(error.Message), cancellationToken);
            var records = new List<CredentialProof>();
            foreach (var kind in kinds.Kinds.Where(kind => kind.Name.StartsWith("backup_destination_history/", StringComparison.Ordinal)
                || kind.Name.StartsWith("projects/", StringComparison.Ordinal) && kind.Name.EndsWith("/backup_destination", StringComparison.Ordinal)))
            {
                var page = await catalog.ReadAsync(transaction => transaction.ListAsync(kind.Name, null, 100, 1024 * 1024, cancellationToken),
                    static error => throw new IOException(error.Message), cancellationToken);
                foreach (var stored in page.Records)
                {
                    var record = EntityPayload.Read(stored, BackupJsonContext.Default.ProjectBackupDestinationRecord)
                        ?? throw new InvalidDataException("credential record missing");
                    var pair = await CustomerCredentials.OpenAsync(keyring, record.Credentials, cancellationToken);
                    records.Add(new(kind.Name + "/" + stored.Id, pair.AccessKeyId, pair.SecretKey,
                        record.Credentials.Ciphertext, keyring.KeyId(record.Credentials.WrappedKey)));
                }
            }
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(records, ProofJsonContext.Default.ListCredentialProof), cancellationToken);
            await Console.Out.WriteLineAsync($"keyring-driver:credentials={records.Count.ToString(CultureInfo.InvariantCulture)}".AsMemory(), cancellationToken);
            return;
        }
    }

    internal static async Task NewCredentialsAsync(string output, CancellationToken cancellationToken)
    {
        TerraBaseOptions.ReadSecretFiles();
        var options = TerraBaseOptions.FromProcess([]);
        var keyring = new MasterKeyring(options.DatabaseKeyring ?? throw new InvalidOperationException("credential proof requires keyring configuration"),
            new KeyRewrapFaults(NullLogger<KeyRewrapFaults>.Instance));
        var credentials = await CustomerCredentials.SealAsync(keyring, "new-access", "new-secret", cancellationToken);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(
            new CompatibilityCredential(credentials.WrappedKey, credentials.Ciphertext, "new-access", "new-secret"),
            ProofJsonContext.Default.CompatibilityCredential), cancellationToken);
    }

    internal static void Rows(string operation)
    {
        TerraBaseOptions.ReadSecretFiles();
        var options = TerraBaseOptions.FromProcess([]);
        var keyring = new MasterKeyring(options.DatabaseKeyring ?? throw new InvalidOperationException("row proof requires keyring configuration"),
            new KeyRewrapFaults(NullLogger<KeyRewrapFaults>.Instance));
        var count = 0;
        foreach (var path in Directory.GetFiles(options.DataDirectory, "*.db", SearchOption.AllDirectories)
            .Where(path => !path.Contains("/.replicas/", StringComparison.Ordinal)))
        {
            var key = keyring.Unwrap(DatabaseKeys.ReadWrapped(path));
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                    EncryptionCipher = "aegis256",
                    EncryptionKey = Convert.ToHexString(key),
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                if (operation == "seed-rows")
                {
                    command.CommandText = "CREATE TABLE IF NOT EXISTS keyring_startup(value INTEGER); INSERT INTO keyring_startup SELECT 42 WHERE NOT EXISTS(SELECT 1 FROM keyring_startup);";
                    command.ExecuteNonQuery();
                }
                command.CommandText = "SELECT value FROM keyring_startup";
                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 42)
                {
                    throw new InvalidDataException("startup database row is missing");
                }
                count++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        Console.WriteLine($"keyring-driver:startup rows=42 databases={count.ToString(CultureInfo.InvariantCulture)}");
    }
}

internal sealed record CredentialProof(string Id, string AccessKeyId, string SecretKey, string Ciphertext, string KeyId);

internal sealed record CompatibilityCredential(string WrappedKey, string Ciphertext, string AccessKeyId, string SecretKey);

[JsonSerializable(typeof(CompatibilityCredential))]
[JsonSerializable(typeof(List<CredentialProof>))]
internal sealed partial class ProofJsonContext : JsonSerializerContext;
