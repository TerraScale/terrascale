using TerraBase.Engine;
using TerraBase.Features.Management;

namespace TerraBase.Features.FileStorage;

internal sealed record ProjectFiles(string OrganizationId, string ProjectId);

internal sealed record FileObjectPurge(string Id, string OrganizationId, string ProjectId, string BucketId, string Region, DateTimeOffset PurgeAfter);

internal delegate ValueTask<bool> FileErasureFence(IStorageTransaction transaction, CancellationToken cancellationToken);

internal enum BucketPurge
{
    Emptied,
    Live,
    Unfenced,
    Spent,
    Unavailable,
}

internal interface IProjectFileErasure
{
    ValueTask<bool> EraseAsync(ProjectFiles project, FileErasureFence fenced, DateTimeOffset until, CancellationToken cancellationToken);
}

internal sealed class FileErasure(SystemCatalog catalog, IFileObjectStore objects, TimeProvider time) : IProjectFileErasure
{
    internal const string Purges = "file_object_purges";
    internal static readonly TimeSpan UploadMargin = TimeSpan.FromMinutes(1);
    private const int Page = 100;
    private const int ParallelDeletes = 16;
    private string? _purgesAfter;

    public async ValueTask<bool> EraseAsync(ProjectFiles project, FileErasureFence fenced, DateTimeOffset until, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(fenced);
        var buckets = FileStorageUsage.Buckets(project.ProjectId);
        while (true)
        {
            var next = await catalog.ReadAsync<NextBucket>(async transaction =>
                await fenced(transaction, cancellationToken).ConfigureAwait(false)
                    ? new(true, (await SystemCatalog.ListAsync(transaction, buckets, null, 1, FileStorageJsonContext.Default.FileBucket, bucket => bucket.Id, cancellationToken).ConfigureAwait(false)).Items is [var first, ..] ? first : null)
                    : new(false, null), static _ => new(false, null), cancellationToken).ConfigureAwait(false);
            if (next.Bucket is not { } bucket)
            {
                return next.Fenced;
            }

            if (await PurgeBucketAsync(project, bucket, fenced, heldOnly: false, until, cancellationToken).ConfigureAwait(false) != BucketPurge.Emptied)
            {
                return false;
            }

            var files = FileStorageUsage.Files(project.OrganizationId, project.ProjectId, bucket.Id);
            if (!await catalog.WriteAsync<bool>(async transaction =>
                {
                    if (!await fenced(transaction, cancellationToken).ConfigureAwait(false)
                        || await SystemCatalog.AnyAsync(transaction, files, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }

                    _ = await transaction.DropKindAsync(files, cancellationToken).ConfigureAwait(false);
                    _ = await transaction.DeleteAsync(buckets, bucket.Id, cancellationToken).ConfigureAwait(false);
                    return true;
                }, static _ => false, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
    }

    internal async ValueTask<BucketPurge> PurgeBucketAsync(
        ProjectFiles project, FileBucket bucket, FileErasureFence fenced, bool heldOnly, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var files = FileStorageUsage.Files(project.OrganizationId, project.ProjectId, bucket.Id);
        while (true)
        {
            var checkedAt = time.GetUtcNow();
            if (checkedAt >= until)
            {
                return BucketPurge.Spent;
            }

            var listed = await catalog.ReadAsync<Listed>(async transaction =>
                await fenced(transaction, cancellationToken).ConfigureAwait(false)
                    ? new(null, (await SystemCatalog.ListAsync(transaction, files, null, Page, FileStorageJsonContext.Default.FileObject, file => file.Id, cancellationToken).ConfigureAwait(false)).Items)
                    : new(BucketPurge.Unfenced, []), static _ => new(BucketPurge.Unavailable, []), cancellationToken).ConfigureAwait(false);
            if (listed.Stop is { } stop)
            {
                return stop;
            }

            if (listed.Files.Count == 0)
            {
                return BucketPurge.Emptied;
            }

            if (heldOnly && listed.Files.Any(file => !Held(file, checkedAt)))
            {
                return BucketPurge.Live;
            }

            await Parallel.ForEachAsync(listed.Files, new ParallelOptions { MaxDegreeOfParallelism = ParallelDeletes, CancellationToken = cancellationToken },
                async (file, token) => await objects.DeleteAsync(bucket.Region, FileStorageUsage.Key(project.OrganizationId, project.ProjectId, bucket.Id, file.Id), token).ConfigureAwait(false))
                .ConfigureAwait(false);
            var removed = await catalog.WriteAsync<BucketPurge?>(async transaction =>
            {
                if (!await fenced(transaction, cancellationToken).ConfigureAwait(false))
                {
                    return BucketPurge.Unfenced;
                }

                foreach (var file in listed.Files)
                {
                    if (await SystemCatalog.GetAsync(transaction, files, file.Id, FileStorageJsonContext.Default.FileObject, cancellationToken).ConfigureAwait(false) is not { } current
                        || heldOnly && !Held(current, checkedAt))
                    {
                        continue;
                    }

                    var writableUntil = current.UploadExpiresAt + UploadMargin;
                    if (writableUntil > checkedAt)
                    {
                        await SystemCatalog.PutAsync(transaction, Purges, current.Id,
                            new FileObjectPurge(current.Id, project.OrganizationId, project.ProjectId, bucket.Id, bucket.Region, writableUntil),
                            FileStorageJsonContext.Default.FileObjectPurge, cancellationToken).ConfigureAwait(false);
                    }

                    _ = await transaction.DeleteAsync(files, current.Id, cancellationToken).ConfigureAwait(false);
                }

                return null;
            }, static _ => BucketPurge.Unavailable, cancellationToken).ConfigureAwait(false);
            if (removed is { } refused)
            {
                return refused;
            }
        }
    }

    internal async ValueTask PurgeLateObjectsAsync(CancellationToken cancellationToken)
    {
        var checkedAt = time.GetUtcNow();
        var page = await catalog.ReadAsync<ManagementPage<FileObjectPurge>?>(async transaction =>
            await SystemCatalog.ListAsync(transaction, Purges, _purgesAfter, Page, FileStorageJsonContext.Default.FileObjectPurge, purge => purge.Id, cancellationToken).ConfigureAwait(false),
            static _ => null, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return;
        }

        await Parallel.ForEachAsync(page.Items, new ParallelOptions { MaxDegreeOfParallelism = ParallelDeletes, CancellationToken = cancellationToken },
            async (purge, token) => await objects.DeleteAsync(purge.Region, FileStorageUsage.Key(purge.OrganizationId, purge.ProjectId, purge.BucketId, purge.Id), token).ConfigureAwait(false))
            .ConfigureAwait(false);
        FileObjectPurge[] settled = [.. page.Items.Where(purge => purge.PurgeAfter <= checkedAt)];
        if (settled.Length > 0)
        {
            _ = await catalog.WriteAsync<bool>(async transaction =>
            {
                foreach (var purge in settled)
                {
                    _ = await transaction.DeleteAsync(Purges, purge.Id, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }, static _ => false, cancellationToken).ConfigureAwait(false);
        }

        _purgesAfter = page.Next;
    }

    private static bool Held(FileObject file, DateTimeOffset at) =>
        file.Status == "deleted" || file.Status == "pending" && file.UploadExpiresAt + UploadMargin <= at;

    private sealed record NextBucket(bool Fenced, FileBucket? Bucket);

    private sealed record Listed(BucketPurge? Stop, IReadOnlyList<FileObject> Files);
}
