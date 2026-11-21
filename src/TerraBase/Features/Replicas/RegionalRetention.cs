using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Primitives;

namespace TerraBase.Features.Replicas;

internal sealed record RetainedManifest(int Count, string Digest);
internal sealed record RetainedEntry(DeletedDatabaseRecord Record, string Revision);
internal sealed record RetainedPage(IReadOnlyList<RetainedEntry> Entries, string? Next);

internal static class RegionalRetention
{
    internal const int PageSize = 100;
    internal const long PageBytes = 256 * 1024;
    internal const int MaxDatabases = 1_000_000;

    internal static void Append(IncrementalHash hash, string key) => hash.AppendData(Encoding.UTF8.GetBytes(key + "\n"));

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
