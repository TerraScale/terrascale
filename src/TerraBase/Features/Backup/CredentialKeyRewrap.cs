using System.Security.Cryptography;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Backup;

internal sealed class CredentialKeyRewrap(SystemCatalog catalog, RegionalOwners owners, IKeyring keyring)
{
    internal async Task<IReadOnlyDictionary<string, long>> RunAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
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
                    await RewrapKindAsync(kind.Name, counts, cancellationToken).ConfigureAwait(false);
                }
            }
            if (!page.HasMore)
            {
                return counts;
            }
            after = page.Kinds[^1].Name;
        }
    }

    private async Task RewrapKindAsync(string kind, Dictionary<string, long> counts, CancellationToken cancellationToken)
    {
        string? after = null;
        while (true)
        {
            async ValueTask<RecordPage> ReadAsync(IStorageTransaction transaction)
            {
                var page = await transaction.ListAsync(kind, after, 100, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                foreach (var stored in page.Records)
                {
                    var record = EntityPayload.Read(stored, BackupJsonContext.Default.ProjectBackupDestinationRecord)
                        ?? throw new InvalidDataException("backup destination credentials are malformed");
                    var wrapped = record.Credentials.WrappedKey;
                    var key = keyring.Unwrap(wrapped);
                    try
                    {
                        var id = keyring.KeyId(wrapped);
                        if (owners.Writer && (id != keyring.ActiveId || !wrapped.StartsWith("v2.", StringComparison.Ordinal)))
                        {
                            await PutAsync(transaction, kind, stored.Id, record with
                            {
                                Credentials = record.Credentials with { WrappedKey = keyring.Wrap(key) },
                            }, BackupJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
                            id = keyring.ActiveId;
                        }
                        counts[id] = counts.GetValueOrDefault(id) + 1;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }
                }
                return page;
            }

            var page = owners.Writer
                ? await catalog.WriteDurableAsync(ReadAsync, static error => throw new IOException(error.Message), cancellationToken).ConfigureAwait(false)
                : await catalog.ReadAsync(ReadAsync, static error => throw new IOException(error.Message), cancellationToken).ConfigureAwait(false);
            if (!page.HasMore)
            {
                return;
            }
            after = page.Records[^1].Id;
        }
    }
}
