using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Regions;
using TerraBase.Primitives;

namespace TerraBase.Features.FileStorage;

internal sealed record ProjectFiles(string OrganizationId, string ProjectId);

internal sealed record FileObjectPurge(string Id, string OrganizationId, string ProjectId, string BucketId, string Region, DateTimeOffset PurgeAfter);

internal sealed record FileProjectPurge(string Id, string OrganizationId, DateTimeOffset Since, DateTimeOffset Until, DateTimeOffset SweepAt);

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

internal sealed class FileErasure(SystemCatalog catalog, IFileObjectStore objects, IRegionCatalog regions, TimeProvider time, FileCleanupFaults faults) : IProjectFileErasure
{
    internal const string Purges = "file_object_purges";
    internal const string ProjectPurges = "file_project_purges";
    internal const string ClosedProjects = "closed_projects";
    internal static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan UploadMargin = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LateObjects = TimeSpan.FromHours(24);
    private static readonly TimeSpan LongestGap = LateObjects / 2;
    private const int Page = 100;
    private const int ParallelDeletes = 16;
    private string? _purgesAfter;
    private string? _projectsAfter;

    public async ValueTask<bool> EraseAsync(ProjectFiles project, FileErasureFence fenced, DateTimeOffset until, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(fenced);
        var buckets = FileStorageUsage.Buckets(project.ProjectId);
        while (true)
        {
            var next = await catalog.ReadAsync<NextBucket>(async transaction =>
                await fenced(transaction, cancellationToken).ConfigureAwait(false)
                    ? new(true, await transaction.GetVersionAsync(ProjectPurges, project.ProjectId, cancellationToken).ConfigureAwait(false) is not null,
                        (await SystemCatalog.ListAsync(transaction, buckets, null, 1, FileStorageJsonContext.Default.FileBucket, bucket => bucket.Id, cancellationToken).ConfigureAwait(false)).Items is [var first, ..] ? first : null)
                    : new(false, false, null), static _ => new(false, false, null), cancellationToken).ConfigureAwait(false);
            if (!next.Fenced)
            {
                return false;
            }

            if (!next.Marked)
            {
                if (!await MarkAsync(project, fenced, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                continue;
            }

            if (next.Bucket is not { } bucket)
            {
                return true;
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

    internal async ValueTask SweepProjectsAsync(CancellationToken cancellationToken)
    {
        var page = await catalog.ReadAsync<ManagementPage<FileProjectPurge>?>(async transaction =>
            await SystemCatalog.ListAsync(transaction, ProjectPurges, _projectsAfter, Page, FileStorageJsonContext.Default.FileProjectPurge, purge => purge.Id, cancellationToken).ConfigureAwait(false),
            static _ => null, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return;
        }

        foreach (var purge in page.Items)
        {
            try
            {
                await SweepProjectAsync(purge, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                faults.Failed(ClosedProjects, purge.Id, null, error);
            }
        }

        _projectsAfter = page.Next;
    }

    private async ValueTask SweepProjectAsync(FileProjectPurge purge, CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        var ended = startedAt >= purge.Until;
        if (!objects.Configured)
        {
            if (ended)
            {
                await MoveAsync(purge, null, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (purge.SweepAt > startedAt)
        {
            return;
        }

        var closed = await catalog.ReadAsync<ClosedProject?>(async transaction =>
            new(await SystemCatalog.GetAsync(transaction, ProjectClosure.Kind, purge.Id, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false)),
            static _ => null, cancellationToken).ConfigureAwait(false);
        if (closed is null)
        {
            return;
        }

        if (closed.Closure is not { FormatVersion: 2 } closure || closure.ProjectId != purge.Id || closure.AuthorityOrganizationId != purge.OrganizationId
            || !EntityIds.IsValid(closure.ProjectId) || !EntityIds.IsValid(closure.AuthorityOrganizationId))
        {
            throw new InvalidOperationException("a file sweep marker names no version-2 closure of its project and organization");
        }

        var swept = await SweepAsync(closure.ProjectId, FileStorageUsage.ProjectObjects(closure.AuthorityOrganizationId, closure.ProjectId), cancellationToken).ConfigureAwait(false);
        if (swept.Cut)
        {
            return;
        }

        if (ended)
        {
            await MoveAsync(purge, swept.Failed ? startedAt + LongestGap : null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var age = startedAt - purge.Since;
        var next = startedAt + (age > SweepPeriod ? age : SweepPeriod);
        await MoveAsync(purge, next < purge.Until ? next : purge.Until, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Swept> SweepAsync(string project, string prefix, CancellationToken cancellationToken)
    {
        var swept = new Swept(false, false);
        foreach (var region in await regions.ServingAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var listed = await objects.ListAsync(region, prefix, cancellationToken).ConfigureAwait(false);
                if (listed.Keys.Any(key => !key.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    throw new IOException("the object store listed a key outside the project's prefix");
                }

                await Parallel.ForEachAsync(listed.Keys, new ParallelOptions { MaxDegreeOfParallelism = ParallelDeletes, CancellationToken = cancellationToken },
                    async (key, token) => await objects.DeleteAsync(region, key, token).ConfigureAwait(false))
                    .ConfigureAwait(false);
                swept = swept with { Cut = swept.Cut || listed.Truncated };
            }
            catch (Exception error) when (FileStorageService.StoreFailure(error) && !cancellationToken.IsCancellationRequested)
            {
                faults.Failed(ClosedProjects, project, region, error);
                swept = swept with { Failed = true };
            }
        }

        return swept;
    }

    private async ValueTask MoveAsync(FileProjectPurge purge, DateTimeOffset? next, CancellationToken cancellationToken) =>
        _ = await catalog.WriteAsync<bool>(async transaction =>
        {
            if (await SystemCatalog.GetAsync(transaction, ProjectPurges, purge.Id, FileStorageJsonContext.Default.FileProjectPurge, cancellationToken).ConfigureAwait(false) != purge)
            {
                return false;
            }

            if (next is not { } sweepAt)
            {
                _ = await transaction.DeleteAsync(ProjectPurges, purge.Id, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await SystemCatalog.PutAsync(transaction, ProjectPurges, purge.Id, purge with { SweepAt = sweepAt },
                FileStorageJsonContext.Default.FileProjectPurge, cancellationToken).ConfigureAwait(false);
            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);

    private ValueTask<bool> MarkAsync(ProjectFiles project, FileErasureFence fenced, CancellationToken cancellationToken) =>
        catalog.WriteAsync<bool>(async transaction =>
        {
            if (!await fenced(transaction, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (await transaction.GetVersionAsync(ProjectPurges, project.ProjectId, cancellationToken).ConfigureAwait(false) is null)
            {
                var since = time.GetUtcNow();
                await SystemCatalog.PutAsync(transaction, ProjectPurges, project.ProjectId,
                    new FileProjectPurge(project.ProjectId, project.OrganizationId, since, since + UploadLifetime + LateObjects, since),
                    FileStorageJsonContext.Default.FileProjectPurge, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, static _ => false, cancellationToken);

    private static bool Held(FileObject file, DateTimeOffset at) =>
        file.Status == "deleted" || file.Status == "pending" && file.UploadExpiresAt + UploadMargin <= at;

    private sealed record NextBucket(bool Fenced, bool Marked, FileBucket? Bucket);

    private sealed record ClosedProject(ProjectClosureRecord? Closure);

    private readonly record struct Swept(bool Cut, bool Failed);

    private sealed record Listed(BucketPurge? Stop, IReadOnlyList<FileObject> Files);
}
