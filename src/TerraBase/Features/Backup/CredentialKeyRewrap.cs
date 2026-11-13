using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Backup;

internal sealed class CredentialKeyRewrap(SystemCatalog catalog, IKeyring keyring)
{
    internal async Task RunAsync(KeyInventory inventory, CancellationToken cancellationToken)
    {
        string? after = null;
        while (true)
        {
            var page = await catalog.ReadAsync(
                transaction => transaction.ListKindsAsync(after, 100, cancellationToken),
                static error => throw new IOException(error.Message), cancellationToken).ConfigureAwait(false);
            foreach (var kind in page.Kinds)
            {
                if (kind.Name.StartsWith("backup_destination_history/", StringComparison.Ordinal)
                    || kind.Name.StartsWith("projects/", StringComparison.Ordinal) && kind.Name.EndsWith("/backup_destination", StringComparison.Ordinal))
                {
                    await RewrapKindAsync(kind.Name, inventory, cancellationToken).ConfigureAwait(false);
                }
            }
            if (!page.HasMore)
            {
                return;
            }
            after = page.Kinds[^1].Name;
        }
    }

    private async Task RewrapKindAsync(string kind, KeyInventory inventory, CancellationToken cancellationToken)
    {
        string? after = null;
        while (true)
        {
            var page = await catalog.ReadAsync(transaction => transaction.ListAsync(kind, after, 100, 1024 * 1024, cancellationToken),
                static error => throw new IOException(error.Message), cancellationToken).ConfigureAwait(false);
            foreach (var stored in page.Records)
            {
                try
                {
                    var record = CustomerCredentials.ReadRecord(stored.Json);
                    var wrapped = record.Credentials.WrappedKey;
                    var key = keyring.Unwrap(wrapped);
                    try
                    {
                        var id = keyring.KeyId(wrapped);
                        if (keyring.NeedsRewrap(wrapped))
                        {
                            id = await catalog.WriteDurableAsync(async transaction =>
                            {
                                var latest = await transaction.GetAsync(kind, stored.Id, cancellationToken).ConfigureAwait(false);
                                if (latest is null)
                                {
                                    return null;
                                }
                                var current = CustomerCredentials.ReadRecord(latest.Json);
                                if (current.Credentials.WrappedKey != wrapped)
                                {
                                    var currentKey = keyring.Unwrap(current.Credentials.WrappedKey);
                                    CryptographicOperations.ZeroMemory(currentKey);
                                    return keyring.KeyId(current.Credentials.WrappedKey);
                                }
                                await PutAsync(transaction, kind, stored.Id, current with
                                {
                                    Credentials = current.Credentials with { WrappedKey = keyring.Rewrap(key, kind + "/" + stored.Id) },
                                }, BackupJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
                                return keyring.ActiveId;
                            }, static error => throw new IOException(error.Message), cancellationToken).ConfigureAwait(false);
                        }
                        if (id is not null)
                        {
                            inventory.Count(id);
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
                {
                    inventory.Unreadable(kind + "/" + stored.Id);
                }
            }
            if (!page.HasMore)
            {
                return;
            }
            after = page.Records[^1].Id;
        }
    }
}
