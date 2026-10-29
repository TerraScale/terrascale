using System.Text.Json;
using Amazon.S3;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Management;
using TerraBase.Features.Qos;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.FileStorage;

internal sealed class FileStorageService(SystemCatalog catalog, IStorageEngine engine, IFileObjectStore objects,
    IStorageQuota quota, TerraBaseOptions options, TimeProvider time, ICatalogGate gate, ILogger<FileStorageService> logger)
    : BackgroundService, IFileStorage
{
    private static readonly Forbidden Denied = new("the bucket rules deny this operation", "access_denied");
    private static readonly NotFound Missing = new("the bucket or file does not exist");
    private static readonly Action<ILogger, Exception?> CleanupIncomplete = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, "FileCleanupIncomplete"), "File storage cleanup could not finish; it will retry");
    private static readonly Unavailable Busy = new("file storage is unavailable; retry");
    private string? _collectionAfter;
    private string? _fileAfter;
    private string? _sweepCollection;
    private static ProjectAccess Access => ProjectAccess.Current ?? throw new InvalidOperationException("verified project identity required");
    private static string Buckets => FileStorageUsage.Buckets(Access.ProjectId);
    private static string Files(string bucket) => FileStorageUsage.Files(Access.OrganizationId, Access.ProjectId, bucket);
    private static string Key(string bucket, string file) => FileStorageUsage.Key(Access.OrganizationId, Access.ProjectId, bucket, file);
    private static bool Admin => !Access.IsEndUser && Access.DatabaseName is null;
    private static ValueTask<FileBucket?> BucketAsync(IStorageTransaction transaction, string bucket, CancellationToken token) =>
        SystemCatalog.GetAsync(transaction, Buckets, bucket, TerraBaseJsonContext.Default.FileBucket, token);
    private static ValueTask<FileObject?> FileAsync(IStorageTransaction transaction, string bucket, string file, CancellationToken token) =>
        SystemCatalog.GetAsync(transaction, Files(bucket), file, TerraBaseJsonContext.Default.FileObject, token);
    private static ValueTask SaveAsync(IStorageTransaction transaction, string bucket, FileObject file, CancellationToken token) =>
        SystemCatalog.PutAsync(transaction, Files(bucket), file.Id, file, TerraBaseJsonContext.Default.FileObject, token);
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
        return CollectionRuleAccess.CollectionWide(policy, Access)
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
            var page = await SystemCatalog.ListAsync(transaction, Buckets, null, 100, TerraBaseJsonContext.Default.FileBucket, b => b.Id, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count >= 100)
            {
                return new Conflict("a project may have at most 100 buckets");
            }
            if (page.Items.Any(b => b.Name == request.Name))
            {
                return new Conflict("a bucket with that name already exists");
            }
            var bucket = new FileBucket(EntityIds.New().ToString(), request.Name!, options.Region, request.Rules);
            await SystemCatalog.PutAsync(transaction, Buckets, bucket.Id, bucket, TerraBaseJsonContext.Default.FileBucket, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CreateCollectionAsync(new CollectionInfo(Files(bucket.Id), time.GetUtcNow().ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
            return bucket;
        }, static error => error, cancellationToken);
    }

    public ValueTask<FileStorageResult> ListBucketsAsync(string? after, CancellationToken cancellationToken) =>
        !Admin ? ValueTask.FromResult<FileStorageResult>(Denied) : catalog.ReadAsync<FileStorageResult>(async transaction =>
        {
            var page = await SystemCatalog.ListAsync(transaction, Buckets, after, 50, TerraBaseJsonContext.Default.FileBucket, b => b.Id, cancellationToken).ConfigureAwait(false);
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
            await SystemCatalog.PutAsync(transaction, Buckets, bucket, saved, TerraBaseJsonContext.Default.FileBucket, cancellationToken).ConfigureAwait(false);
            return saved;
        }, static error => error, cancellationToken);
    }

    public ValueTask<FileStorageResult> DeleteBucketAsync(string bucket, CancellationToken cancellationToken) =>
        !Admin ? ValueTask.FromResult<FileStorageResult>(Denied) : catalog.WriteAsync<FileStorageResult>(async transaction =>
        {
            if (await BucketAsync(transaction, bucket, cancellationToken).ConfigureAwait(false) is null)
            {
                return Missing;
            }
            if (await SystemCatalog.AnyAsync(transaction, Files(bucket), cancellationToken).ConfigureAwait(false))
            {
                return new Conflict("delete the files and wait for upload cleanup before deleting the bucket");
            }
            _ = await transaction.DropCollectionAsync(Files(bucket), cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Buckets, bucket, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken);

    public async ValueTask<FileStorageResult> CreateUploadAsync(string bucket, CreateFileUploadRequest request, CancellationToken cancellationToken)
    {
        if (request.Name is not { Length: > 0 and <= 255 } || request.Name.Any(char.IsControl)
            || request.ContentType is not { Length: > 0 and <= 128 } || request.ContentType.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || request.Size is < 1 or > 100 * 1024 * 1024 || request.Metadata.ValueKind != JsonValueKind.Object
            || request.Metadata.GetRawText().Length > 16 * 1024 || !Checksum(request.Sha256))
        {
            return new Validation("provide name, contentType, size (1–104857600), base64 SHA-256 and object metadata (at most 16 KiB)");
        }
        var file = new FileObject(EntityIds.New().ToString(), request.Name, request.ContentType, request.Size, request.Sha256!, request.Metadata.Clone(), "pending", time.GetUtcNow().AddMinutes(5));
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
            if (currentBucket is null || current is null || current.Status == "deleted")
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
            if (Access.DatabaseName is not null || Access.IsEndUser && !CollectionRuleAccess.Granted(definition.Rules, Access, write: false))
            {
                return Denied;
            }
            DocumentFilter filter = new CompareFilter(new DocumentPath(["status"]), Comparison.Equal, new ScalarValue(ScalarKind.Text, "ready"));
            if (Access.IsEndUser && !CollectionRuleAccess.CollectionWide(definition.Rules.Read, Access))
            {
                var owner = definition.Rules.Owner!.Split('/').Skip(1).Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
                filter = new AndFilter([filter, new CompareFilter(new DocumentPath(["metadata", .. owner]), Comparison.Equal, new ScalarValue(ScalarKind.Text, Access.Actor))]);
            }
            var page = await transaction.QueryAsync(new DocumentQuery(Files(bucket), filter, [], after is null ? null : new QueryPosition([], after), 50, 1024 * 1024), cancellationToken).ConfigureAwait(false);
            var items = page.Rows.Select(row => EntityPayload.Read(row.Document, TerraBaseJsonContext.Default.FileObject)!).ToArray();
            return new FileObjectPage(items, page.HasMore ? items[^1].Id : null);
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

    private static bool StoreFailure(Exception error) => error is AmazonS3Exception or HttpRequestException or IOException;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!gate.IsOpen)
            {
                continue;
            }
            try { await SweepAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { /* One bounded cleanup pass expired; retry on the next tick. */ }
            catch (Exception error) when (StoreFailure(error) || error is QueryCapacityException)
            {
                CleanupIncomplete(logger, null);
            }
        }
    }

    private async ValueTask SweepAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var token = budget.Token;
        if (_sweepCollection is null)
        {
            var database = await engine.OpenSystemDatabaseAsync(token).ConfigureAwait(false);
            await using var read = await database.BeginTransactionAsync(write: false, token).ConfigureAwait(false);
            var (collections, hasMore) = await read.ListCollectionsAsync(_collectionAfter, 50, token).ConfigureAwait(false);
            var found = collections.FirstOrDefault(c => c.Name.StartsWith(FileStorageUsage.Prefix, StringComparison.Ordinal));
            _collectionAfter = found?.Name ?? (hasMore ? collections[^1].Name : null);
            if (found is null)
            {
                return;
            }
            _sweepCollection = found.Name;
            _fileAfter = null;
        }
        var collection = _sweepCollection;
        var parts = collection.Split('/');
        if (parts.Length != 4) { _sweepCollection = null; return; }
        var pageFiles = await catalog.ReadAsync<ManagementPage<FileObject>?>(
            async tx => await SystemCatalog.ListAsync(tx, collection, _fileAfter, 50, TerraBaseJsonContext.Default.FileObject, f => f.Id, token).ConfigureAwait(false), static _ => null, token).ConfigureAwait(false);
        if (pageFiles is null)
        {
            return;
        }
        var definition = await catalog.ReadAsync<FileBucket?>(
            tx => SystemCatalog.GetAsync(tx, FileStorageUsage.Buckets(parts[2]), parts[3], TerraBaseJsonContext.Default.FileBucket, token), static _ => null, token).ConfigureAwait(false);
        if (definition is null)
        {
            _sweepCollection = null;
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
                var current = await SystemCatalog.GetAsync(tx, collection, file.Id, TerraBaseJsonContext.Default.FileObject, token).ConfigureAwait(false);
                if (current is not null && current.Status == file.Status)
                {
                    await tx.DeleteAsync(collection, file.Id, token).ConfigureAwait(false);
                }
                return true;
            }, static _ => false, token).ConfigureAwait(false);
        }
        _fileAfter = pageFiles.Next;
        if (_fileAfter is null)
        {
            _sweepCollection = null;
        }
    }
}
