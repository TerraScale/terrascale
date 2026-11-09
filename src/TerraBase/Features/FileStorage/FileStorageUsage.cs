using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Features.Management;
using TerraBase.Hosting;

namespace TerraBase.Features.FileStorage;

public interface IFileStorageUsage
{
    ValueTask<long> BytesAsync(string organization, string? project, CancellationToken cancellationToken);
}

internal sealed class FileStorageUsage(IStorageEngine engine) : IFileStorageUsage
{
    internal const string Prefix = "file-storage/";
    internal static string Buckets(string project) => SystemCatalog.ProjectState(project) + "file-buckets";
    internal static string Files(string organization, string project, string bucket) => $"{Prefix}{organization}/{project}/{bucket}";
    internal static string Key(string organization, string project, string bucket, string file) => $"app-files/{organization}/{project}/{bucket}/{file}";

    public async ValueTask<long> BytesAsync(string organization, string? project, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var prefix = Prefix + organization + "/" + (project is null ? "" : project + "/");
        var total = 0L;
        var visited = 0;
        string? after = null;
        bool more;
        do
        {
            var (kinds, hasMore) = await transaction.ListKindsAsync(after, 100, cancellationToken).ConfigureAwait(false);
            more = hasMore;
            foreach (var kind in kinds)
            {
                if (++visited > 10_000)
                {
                    throw new QueryCapacityException("file storage accounting exceeded its bucket budget") { Deterministic = true };
                }
                after = kind.Name;
                if (!kind.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                string? fileAfter = null;
                RecordPage files;
                do
                {
                    files = await transaction.ListAsync(kind.Name, fileAfter, 100, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                    foreach (var saved in files.Records)
                    {
                        if (++visited > 100_000)
                        {
                            throw new QueryCapacityException("file storage accounting exceeded its object budget") { Deterministic = true };
                        }
                        var file = EntityPayload.Read(saved, TerraBaseJsonContext.Default.FileObject)!;
                        total = checked(total + file.Size);
                        fileAfter = saved.Id;
                    }
                } while (files.HasMore);
            }
        } while (more);
        return total;
    }
}
