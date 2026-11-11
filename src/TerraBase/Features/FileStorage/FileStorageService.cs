using Amazon.Runtime;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Catalog;
using TerraBase.Features.FileStorage;
using TerraBase.Features.Management;
using TerraBase.Features.Qos;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.FileStorage;

internal sealed class FileStorageService(SystemCatalog catalog, IStorageEngine engine, IFileObjectStore objects,
    IStorageQuota quota, TerraBaseOptions options, TimeProvider time, ICatalogGate gate, FileErasure erasure, IMeterFactory meters, ILogger<FileStorageService> logger)
    : BackgroundService, IFileStorage
{
    private const int ListPage = 50;
    private const int MaxListScan = 1000;
    private static readonly Forbidden Denied = new("the bucket rules deny this operation", "access_denied");
    private static readonly NotFound Missing = new("the bucket or file does not exist");
    private static readonly Conflict LiveFiles = new("delete the bucket's files and let uploads in progress complete or expire before deleting the bucket");
    private static readonly Action<ILogger, string, Exception?> CleanupIncomplete = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1, "FileCleanupIncomplete"), "File storage cleanup of {Pass} could not finish; the next pass tries again");
    private static readonly Unavailable Busy = new("file storage is unavailable; retry");
    private static readonly TimeSpan BucketErasureBudget = TimeSpan.FromSeconds(10);
    private readonly Counter<long> _cleanupErrors = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.file_storage.cleanup_errors");
    private string? _kindAfter;
    private string? _fileAfter;
    private string? _sweepKind;
    private static ProjectAccess Access => ProjectAccess.Current ?? throw new InvalidOperationException("verified project identity required");
    private static string Buckets => FileStorageUsage.Buckets(Access.ProjectId);
    private static string Files(string bucket) => FileStorageUsage.Files(Access.OrganizationId, Access.ProjectId, bucket);
    private static string Key(string bucket, string file) => FileStorageUsage.Key(Access.OrganizationId, Access.ProjectId, bucket, file);
    private static bool Admin => !Access.IsEndUser && Access.DatabaseName is null;
    private static ValueTask<FileBucket?> BucketAsync(IStorageTransaction transaction, string bucket, CancellationToken token) =>
        SystemCatalog.GetAsync(transaction, Buckets, bucket, FileStorageJsonContext.Default.FileBucket, token);
    private static ValueTask<FileObject?> FileAsync(IStorageTransaction transaction, string bucket, string file, CancellationToken token) =>
        SystemCatalog.GetAsync(transaction, Files(bucket), file, FileStorageJsonContext.Default.FileObject, token);
    private static ValueTask SaveAsync(IStorageTransaction transaction, string bucket, FileObject file, CancellationToken token) =>
        SystemCatalog.PutAsync(transaction, Files(bucket), file.Id, file, FileStorageJsonContext.Default.FileObject, token);
    private static bool Allowed(FileBucket bucket, FileObject file, bool write)
    {
        if (Access.DatabaseName is not null)
        {
            return false;
        }
        if (!Access.IsEndUser)
        {
            return true;
        }
        var policy = write ? bucket.Rules.Write : bucket.Rules.Read;
        return BucketWide(policy, Access)
            || policy?.Owner == true && bucket.Rules.Subject(JsonSerializer.SerializeToUtf8Bytes(file.Metadata, TerraBaseJsonContext.Default.JsonElement)) == Access.Actor;
    }

    public ValueTask<FileStorageResult> CreateBucketAsync(CreateFileBucketRequest request, CancellationToken cancellationToken)
    {
        if (!Admin)
        {
            return ValueTask.FromResult<FileStorageResult>(Denied);
        }
        if (!DatabaseName.TryParse(request.Name, out _) || request.Rules is null)
        {
            return ValueTask.FromResult<FileStorageResult>(new Validation("name must be a lowercase name of at most 63 characters; rules are required"));
        }
        if (request.Rules.Check() is { } invalid)
        {
            return ValueTask.FromResult<FileStorageResult>(invalid);
        }
        return catalog.WriteAsync<FileStorageResult>(async transaction =>
        {
            if (await ProjectClosure.IsClosedAsync(transaction, Access.ProjectId, cancellationToken).ConfigureAwait(false))
            {
                return Missing;
            }
            var page = await SystemCatalog.ListAsync(transaction, Buckets, null, 100, FileStorageJsonContext.Default.FileBucket, b => b.Id, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count >= 100)
            {
                return new Conflict("a project may have at most 100 buckets");
            }
            if (page.Items.Any(b => b.Name == request.Name))
            {
                return new Conflict("a bucket with that name already exists");
            }
            var bucket = new FileBucket(EntityIds.New().ToString(), request.Name!, options.Region, request.Rules);
            await SystemCatalog.PutAsync(transaction, Buckets, bucket.Id, bucket, FileStorageJsonContext.Default.FileBucket, cancellationToken).ConfigureAwait(false);
            return bucket;
        }, static error => error, cancellationToken);
    }

    public ValueTask<FileStorageResult> ListBucketsAsync(string? after, CancellationToken cancellationToken) =>
        !Admin ? ValueTask.FromResult<FileStorageResult>(Denied) : catalog.ReadAsync<FileStorageResult>(async transaction =>
        {
            var page = await SystemCatalog.ListAsync(transaction, Buckets, after, 50, FileStorageJsonContext.Default.FileBucket, b => b.Id, cancellationToken).ConfigureAwait(false);
            return new FileBucketPage(page.Items, page.Next);
        }, static error => error, cancellationToken);

    public ValueTask<FileStorageResult> SetRulesAsync(string bucket, FileBucketRulesRequest request, CancellationToken cancellationToken)
    {
        if (!Admin)
        {
            return ValueTask.FromResult<FileStorageResult>(Denied);
        }
        if (request.Rules is null)
        {
            return ValueTask.FromResult<FileStorageResult>(new Validation("rules are required"));
        }
        if (request.Rules.Check() is { } invalid)
        {
            return ValueTask.FromResult<FileStorageResult>(invalid);
        }
        return catalog.WriteAsync<FileStorageResult>(async transaction =>
        {
            if (await BucketAsync(transaction, bucket, cancellationToken).ConfigureAwait(false) is not { } saved)
            {
                return Missing;
            }
            if (saved.Rules.Owner != request.Rules.Owner)
            {
                return new Conflict("the owner pointer is immutable");
            }
            saved = saved with { Rules = request.Rules };
            await SystemCatalog.PutAsync(transaction, Buckets, bucket, saved, FileStorageJsonContext.Default.FileBucket, cancellationToken).ConfigureAwait(false);
            return saved;
        }, static error => error, cancellationToken);
    }

    public async ValueTask<FileStorageResult> DeleteBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        if (!Admin)
        {
            return Denied;
        }
        var found = await catalog.ReadAsync<FileStorageResult>(async transaction =>
            await BucketAsync(transaction, bucket, cancellationToken).ConfigureAwait(false) is { } saved ? saved : Missing, static error => error, cancellationToken).ConfigureAwait(false);
        if (found is not FileBucket definition)
        {
            return found;
        }
        BucketPurge purged;
        try
        {
            purged = await erasure.PurgeBucketAsync(new(Access.OrganizationId, Access.ProjectId), definition,
                async (transaction, token) => await BucketAsync(transaction, bucket, token).ConfigureAwait(false) is not null,
                heldOnly: true, time.GetUtcNow() + BucketErasureBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (StoreFailure(error)) { return Busy; }
        return purged switch
        {
            BucketPurge.Emptied => await catalog.WriteAsync<FileStorageResult>(async transaction =>
            {
                if (await BucketAsync(transaction, bucket, cancellationToken).ConfigureAwait(false) is null)
                {
                    return Missing;
                }
                if (await SystemCatalog.AnyAsync(transaction, Files(bucket), cancellationToken).ConfigureAwait(false))
                {
                    return LiveFiles;
                }
                _ = await transaction.DropKindAsync(Files(bucket), cancellationToken).ConfigureAwait(false);
                await transaction.DeleteAsync(Buckets, bucket, cancellationToken).ConfigureAwait(false);
                return new Done();
            }, static error => error, cancellationToken).ConfigureAwait(false),
            BucketPurge.Live => LiveFiles,
            BucketPurge.Unfenced => Missing,
            BucketPurge.Spent => new Unavailable("the bucket's deleted files are still being erased; retry"),
            BucketPurge.Unavailable => Busy,
            _ => throw new UnreachableException(),
        };
    }

    public async ValueTask<FileStorageResult> CreateUploadAsync(string bucket, CreateFileUploadRequest request, CancellationToken cancellationToken)
    {
        if (request.Name is not { Length: > 0 and <= 255 } || request.Name.Any(char.IsControl)
            || request.ContentType is not { Length: > 0 and <= 128 } || request.ContentType.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || request.Size is < 1 or > 100 * 1024 * 1024 || request.Metadata.ValueKind != JsonValueKind.Object
            || request.Metadata.GetRawText().Length > 16 * 1024 || !Checksum(request.Sha256))
        {
            return new Validation("provide name, contentType, size (1–104857600), base64 SHA-256 and object metadata (at most 16 KiB)");
        }
        var file = new FileObject(EntityIds.New().ToString(), request.Name, request.ContentType, request.Size, request.Sha256!, request.Metadata.Clone(), "pending", time.GetUtcNow() + FileErasure.UploadLifetime);
        var authorized = await catalog.ReadAsync<FileStorageResult>(async transaction =>
        {
            var saved = await BucketAsync(transaction, bucket, cancellationToken).ConfigureAwait(false);
            return saved is null ? Missing : CanCreate(saved, file) ? saved : Denied;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (authorized is not FileBucket definition)
        {
            return authorized;
        }
        FileSignedUrl signed;
        try { signed = await objects.UploadAsync(definition.Region, Key(bucket, file.Id), file, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (StoreFailure(error)) { return Busy; }
        var admission = await quota.EnterOrganizationsAsync([Access.OrganizationId], cancellationToken).ConfigureAwait(false);
        if (admission is Unavailable unavailable)
        {
            return unavailable;
        }
        using var lease = admission switch { StorageQuotaGroupLease accepted => accepted, Unavailable => throw new InvalidOperationException() };
        var checkedQuota = await lease.CheckAsync(Access.OrganizationId, file.Size, cancellationToken).ConfigureAwait(false);
        if (checkedQuota is QuotaExceeded exceeded)
        {
            return exceeded;
        }
        if (checkedQuota is Unavailable refused)
        {
            return refused;
        }
        var savedResult = await catalog.WriteAsync<FileStorageResult>(async transaction =>
        {
            var current = await BucketAsync(transaction, bucket, cancellationToken).ConfigureAwait(false);
            if (current is null || await ProjectClosure.IsClosedAsync(transaction, Access.ProjectId, cancellationToken).ConfigureAwait(false))
            {
                return Missing;
            }
            if (!CanCreate(current, file))
            {
                return Denied;
            }
            await SaveAsync(transaction, bucket, file, cancellationToken).ConfigureAwait(false);
            return signed;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        return savedResult;
    }

    private static bool CanCreate(FileBucket bucket, FileObject file) => Allowed(bucket, file, write: true)
        && (!Access.IsEndUser || bucket.Rules.Owner is null
            || bucket.Rules.Subject(JsonSerializer.SerializeToUtf8Bytes(file.Metadata, TerraBaseJsonContext.Default.JsonElement)) == Access.Actor);

    public async ValueTask<FileStorageResult> CompleteUploadAsync(string bucket, string file, CancellationToken cancellationToken)
    {
        var found = await ReadFileAsync(bucket, file, write: true, cancellationToken).ConfigureAwait(false);
        if (found is not FileObject saved)
        {
            return found;
        }
        if (saved.Status == "ready")
        {
            return saved;
        }
        if (saved.UploadExpiresAt <= time.GetUtcNow())
        {
            return new Conflict("the upload reservation expired");
        }
        var definition = await catalog.ReadAsync<FileBucket?>(transaction => BucketAsync(transaction, bucket, cancellationToken), static _ => null, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return Missing;
        }
        try
        {
            if (!await objects.VerifyAsync(definition.Region, Key(bucket, file), saved, cancellationToken).ConfigureAwait(false))
            {
                return new Conflict("the uploaded file does not match the reserved size and checksum");
            }
        }
        catch (Exception error) when (StoreFailure(error)) { return Busy; }
        return await catalog.WriteAsync<FileStorageResult>(async transaction =>
        {
            var currentBucket = await BucketAsync(transaction, bucket, cancellationToken).ConfigureAwait(false);
            var current = await FileAsync(transaction, bucket, file, cancellationToken).ConfigureAwait(false);
            if (currentBucket is null || current is null || current.Status == "deleted"
                || await ProjectClosure.IsClosedAsync(transaction, Access.ProjectId, cancellationToken).ConfigureAwait(false))
            {
                return Missing;
            }
            if (!Allowed(currentBucket, current, write: true))
            {
                return Denied;
            }
            if (current.Status == "pending" && current.UploadExpiresAt <= time.GetUtcNow())
            {
                return new Conflict("the upload reservation expired");
            }
            var ready = current with { Status = "ready" };
            await SaveAsync(transaction, bucket, ready, cancellationToken).ConfigureAwait(false);
            return ready;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<FileStorageResult> ListFilesAsync(string bucket, string? after, CancellationToken cancellationToken) =>
        catalog.ReadAsync<FileStorageResult>(async transaction =>
        {
            if (await BucketAsync(transaction, bucket, cancellationToken).ConfigureAwait(false) is not { } definition)
            {
                return Missing;
            }
            if (Access.DatabaseName is not null || Access.IsEndUser && !Granted(definition.Rules, Access, write: false))
            {
                return Denied;
            }
            List<FileObject> items = [];
            var cursor = after;
            for (var scanned = 0; scanned < MaxListScan;)
            {
                var page = await transaction.ListAsync(Files(bucket), cursor, 100, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                foreach (var stored in page.Records)
                {
                    scanned++;
                    cursor = stored.Id;
                    if (EntityPayload.Read(stored, FileStorageJsonContext.Default.FileObject) is { Status: "ready" } file && Allowed(definition, file, write: false))
                    {
                        items.Add(file);
                        if (items.Count == ListPage)
                        {
                            return new FileObjectPage(items, page.HasMore || stored.Id != page.Records[^1].Id ? file.Id : null);
                        }
                    }
                }

                if (!page.HasMore)
                {
                    return new FileObjectPage(items, null);
                }
            }

            return new FileObjectPage(items, cursor);
        }, static error => error, cancellationToken);

    private ValueTask<FileStorageResult> ReadFileAsync(string bucket, string file, bool write, CancellationToken cancellationToken) =>
        catalog.ReadAsync<FileStorageResult>(async transaction =>
        {
            if (await BucketAsync(transaction, bucket, cancellationToken).ConfigureAwait(false) is not { } definition
                || await FileAsync(transaction, bucket, file, cancellationToken).ConfigureAwait(false) is not { } saved || saved.Status == "deleted")
            {
                return Missing;
            }
            return Allowed(definition, saved, write) ? saved : Denied;
        }, static error => error, cancellationToken);

    public async ValueTask<FileStorageResult> DownloadAsync(string bucket, string file, CancellationToken cancellationToken)
    {
        var found = await ReadFileAsync(bucket, file, write: false, cancellationToken).ConfigureAwait(false);
        if (found is not FileObject saved)
        {
            return found;
        }
        if (saved.Status != "ready")
        {
            return new Conflict("the upload is not complete");
        }
        var definition = await catalog.ReadAsync<FileBucket?>(transaction => BucketAsync(transaction, bucket, cancellationToken), static _ => null, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return Missing;
        }
        try { return await objects.DownloadAsync(definition.Region, Key(bucket, file), saved, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (StoreFailure(error)) { return Busy; }
    }

    public ValueTask<FileStorageResult> DeleteFileAsync(string bucket, string file, CancellationToken cancellationToken) =>
        catalog.WriteAsync<FileStorageResult>(async transaction =>
        {
            if (await BucketAsync(transaction, bucket, cancellationToken).ConfigureAwait(false) is not { } definition
                || await FileAsync(transaction, bucket, file, cancellationToken).ConfigureAwait(false) is not { } saved)
            {
                return Missing;
            }
            if (!Allowed(definition, saved, write: true))
            {
                return Denied;
            }
            if (saved.Status == "deleted")
            {
                return new Done();
            }
            var after = time.GetUtcNow().AddMinutes(6);
            if (saved.UploadExpiresAt > after)
            {
                after = saved.UploadExpiresAt.AddMinutes(1);
            }
            await SaveAsync(transaction, bucket, saved with { Status = "deleted", DeleteAfter = after }, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken);

    private static bool Checksum(string? checksum)
    {
        Span<byte> bytes = stackalloc byte[32];
        return checksum is { Length: 44 } && Convert.TryFromBase64String(checksum, bytes, out var written) && written == 32;
    }

    private static bool StoreFailure(Exception error) => error is AmazonServiceException or AmazonClientException or HttpRequestException or IOException;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task[] passes =
        [
            RunEveryAsync("expired_files", TimeSpan.FromSeconds(30), SweepAsync, stop.Token),
            RunEveryAsync("late_objects", TimeSpan.FromSeconds(5), erasure.PurgeLateObjectsAsync, stop.Token),
            RunEveryAsync("closed_projects", FileErasure.SweepPeriod, erasure.SweepProjectsAsync, stop.Token),
        ];
        _ = await Task.WhenAny(passes).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(passes).ConfigureAwait(false);
    }

    private async Task RunEveryAsync(string name, TimeSpan period, Func<CancellationToken, ValueTask> pass, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(period, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!gate.IsOpen)
            {
                continue;
            }
            try { await pass(stoppingToken).ConfigureAwait(false); }
            catch (Exception error) when (StoreFailure(error) || error is QueryCapacityException)
            {
                _cleanupErrors.Add(1, new KeyValuePair<string, object?>("pass", name));
                CleanupIncomplete(logger, name, error);
            }
        }
    }

    private async ValueTask SweepAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await SweepPageAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async ValueTask SweepPageAsync(CancellationToken token)
    {
        if (_sweepKind is null)
        {
            var database = await engine.OpenSystemDatabaseAsync(token).ConfigureAwait(false);
            await using var read = await database.BeginTransactionAsync(write: false, token).ConfigureAwait(false);
            var (kinds, hasMore) = await read.ListKindsAsync(_kindAfter, 50, token).ConfigureAwait(false);
            var found = kinds.FirstOrDefault(c => c.Name.StartsWith(FileStorageUsage.Prefix, StringComparison.Ordinal));
            _kindAfter = found?.Name ?? (hasMore ? kinds[^1].Name : null);
            if (found is null)
            {
                return;
            }
            _sweepKind = found.Name;
            _fileAfter = null;
        }
        var kind = _sweepKind;
        var parts = kind.Split('/');
        if (parts.Length != 4) { _sweepKind = null; return; }
        var pageFiles = await catalog.ReadAsync<ManagementPage<FileObject>?>(
            async tx => await SystemCatalog.ListAsync(tx, kind, _fileAfter, 50, FileStorageJsonContext.Default.FileObject, f => f.Id, token).ConfigureAwait(false), static _ => null, token).ConfigureAwait(false);
        if (pageFiles is null)
        {
            return;
        }
        var definition = await catalog.ReadAsync<FileBucket?>(
            tx => SystemCatalog.GetAsync(tx, FileStorageUsage.Buckets(parts[2]), parts[3], FileStorageJsonContext.Default.FileBucket, token), static _ => null, token).ConfigureAwait(false);
        if (definition is null)
        {
            _sweepKind = null;
            return;
        }
        foreach (var file in pageFiles.Items)
        {
            var now = time.GetUtcNow();
            if (file.Status == "ready" || file.Status == "pending" && file.UploadExpiresAt.AddMinutes(1) > now
                || file.Status == "deleted" && file.DeleteAfter > now)
            {
                continue;
            }
            await objects.DeleteAsync(definition.Region, FileStorageUsage.Key(parts[1], parts[2], parts[3], file.Id), token).ConfigureAwait(false);
            await catalog.WriteAsync<bool>(async tx =>
            {
                var current = await SystemCatalog.GetAsync(tx, kind, file.Id, FileStorageJsonContext.Default.FileObject, token).ConfigureAwait(false);
                if (current is not null && current.Status == file.Status)
                {
                    await tx.DeleteAsync(kind, file.Id, token).ConfigureAwait(false);
                }
                return true;
            }, static _ => false, token).ConfigureAwait(false);
        }
        _fileAfter = pageFiles.Next;
        if (_fileAfter is null)
        {
            _sweepKind = null;
        }
    }

    private static bool BucketWide(FileAccessPolicy? policy, ProjectAccess access) =>
        policy is not null && (policy.Public || policy.Roles is { } roles && roles.Any(access.Roles.Contains));

    private static bool Granted(FileAccessRules? rules, ProjectAccess access, bool write) =>
        (write ? rules?.Write : rules?.Read) is { } policy && (BucketWide(policy, access) || policy.Owner && access.Actor.Length > 0);
}
