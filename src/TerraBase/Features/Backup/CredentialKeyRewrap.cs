using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Functions;
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
                    await RewrapKindAsync(kind.Name, Destinations, inventory, cancellationToken).ConfigureAwait(false);
                }
                else if (FunctionSecretSeal.IsKind(kind.Name))
                {
                    await RewrapKindAsync(kind.Name, FunctionSecrets, inventory, cancellationToken).ConfigureAwait(false);
                }
            }
            if (!page.HasMore)
            {
                return;
            }
            after = page.Kinds[^1].Name;
        }
    }

    private sealed record Envelope(Func<StoredRecord, string> WrappedKey,
        Func<IStorageTransaction, string, StoredRecord, string, CancellationToken, ValueTask> RewriteAsync);

    private static readonly Envelope Destinations = new(
        static stored => CustomerCredentials.ReadRecord(stored.Json).Credentials.WrappedKey,
        static (transaction, kind, stored, wrapped, cancellationToken) =>
        {
            var current = CustomerCredentials.ReadRecord(stored.Json);
            return PutAsync(transaction, kind, stored.Id, current with { Credentials = current.Credentials with { WrappedKey = wrapped } },
                BackupJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken);
        });

    private static readonly Envelope FunctionSecrets = new(
        static stored => FunctionSecretSeal.Read(stored.Json.Span).WrappedKey,
        static (transaction, kind, stored, wrapped, cancellationToken) => PutAsync(transaction, kind, stored.Id,
            FunctionSecretSeal.Read(stored.Json.Span) with { WrappedKey = wrapped }, FunctionsJsonContext.Default.FunctionSecretRecord, cancellationToken));

    private async Task RewrapKindAsync(string kind, Envelope envelope, KeyInventory inventory, CancellationToken cancellationToken)
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
                    var wrapped = envelope.WrappedKey(stored);
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
                                var currentWrapped = envelope.WrappedKey(latest);
                                if (currentWrapped != wrapped)
                                {
                                    var currentKey = keyring.Unwrap(currentWrapped);
                                    CryptographicOperations.ZeroMemory(currentKey);
                                    return keyring.KeyId(currentWrapped);
                                }
                                await envelope.RewriteAsync(transaction, kind, latest, keyring.Rewrap(key, kind + "/" + stored.Id), cancellationToken)
                                    .ConfigureAwait(false);
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
