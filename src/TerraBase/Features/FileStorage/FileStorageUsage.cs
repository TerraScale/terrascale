using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;

namespace TerraBase.Features.FileStorage;

/// <summary>Logical file bytes including unexpired upload reservations; replicas add no bytes.</summary>
public interface IFileStorageUsage
{
    /// <summary>Reads bounded logical accounting for an organization or project.</summary>
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
            var page = await transaction.ListCollectionsAsync(after, 100, cancellationToken).ConfigureAwait(false);
            more = page.HasMore;
            foreach (var collection in page.Collections)
            {
                if (++visited > 10_000) throw new QueryCapacityException("file storage accounting exceeded its collection budget");
                after = collection.Name;
                if (!collection.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string? fileAfter = null;
                DocumentPage files;
                do
                {
                    files = await transaction.ListAsync(collection.Name, fileAfter, 100, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                    foreach (var saved in files.Documents)
                    {
                        if (++visited > 100_000) throw new QueryCapacityException("file storage accounting exceeded its object budget");
                        var file = Primitives.EntityPayload.Read(saved, TerraBaseJsonContext.Default.FileObject)!;
                        total = checked(total + file.Size);
                        fileAfter = saved.Id;
                    }
                } while (files.HasMore);
            }
        } while (more);
        return total;
    }
}
