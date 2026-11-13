using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Primitives;

namespace TerraBase.Features.Replicas;

internal sealed record RetainedManifest(int Count, string Digest);
internal sealed record RetainedEntry(DeletedDatabaseRecord Record, string Revision);
internal sealed record RetainedCertification(int Count, string Digest, int Missing, double OldestMissingSeconds);
internal sealed record RetainedPage(IReadOnlyList<RetainedEntry> Entries, string? Next);

internal static class RegionalRetention
{
    internal const int PageSize = 100;
    internal const long PageBytes = 256 * 1024;
    internal const int MaxDatabases = 1_000_000;

    internal static void Append(IncrementalHash hash, string key, string revision) => hash.AppendData(Encoding.UTF8.GetBytes(key + "\n" + revision + "\n"));

    internal static async Task<RetainedManifest> ManifestAsync(IStorageEngine engine, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        string? after = null;
        do
        {
            var page = await RecordsAsync(engine, after, token).ConfigureAwait(false);
            foreach (var record in page.Records)
            {
                if (++count > MaxDatabases)
                {
                    throw new IOException("retained handoff database bound exceeded");
                }
                Append(hash, record.Id, Read(record).Revision);
            }
            after = page.HasMore ? page.Records[^1].Id : null;
        }
        while (after is not null);
        return new RetainedManifest(count, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    internal static async Task<RecordPage> RecordsAsync(IStorageEngine engine, string? after, CancellationToken token)
    {
        var store = await engine.OpenSystemDatabaseAsync(token).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, token).ConfigureAwait(false);
        return await transaction.ListAsync(DeletedDatabases.Kind, after, PageSize, PageBytes, token).ConfigureAwait(false);
    }

    internal static DeletedDatabaseRecord Read(StoredRecord record) =>
        EntityPayload.Read(record, ManagementJsonContext.Default.DeletedDatabaseRecord)
            ?? throw new InvalidDataException("invalid retained database");
}
