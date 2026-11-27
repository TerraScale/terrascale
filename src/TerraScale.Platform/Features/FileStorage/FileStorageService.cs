using System.Text;
using TerraScale.Platform.Features.Functions;

namespace TerraScale.Platform.Features.FileStorage;

public sealed record BucketDeleted;

public sealed record FileDeleted;

public readonly union CreateBucketOutcome(
    FileBucket,
    PlatformInvalidRequest,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public readonly union ListBucketsOutcome(FileBucketPage, PlatformForbidden, PlatformUnavailable);

public readonly union SetBucketRulesOutcome(
    FileBucket,
    PlatformInvalidRequest,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public readonly union DeleteBucketOutcome(
    BucketDeleted,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public readonly union CreateUploadOutcome(
    UploadCreated,
    PlatformInvalidRequest,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public readonly union CompleteUploadOutcome(
    FileObject,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public readonly union ListFilesOutcome(FileObjectPage, PlatformForbidden, PlatformNotFound, PlatformUnavailable);

public readonly union CreateDownloadOutcome(
    FileSignedUrl,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public readonly union DeleteFileOutcome(FileDeleted, PlatformForbidden, PlatformNotFound, PlatformUnavailable);

public sealed class FileStorageService(PlatformStore store, S3FileObjectStore objects, PlatformOptions options, PlatformMetrics metrics, TimeProvider timeProvider)
{
    public static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan UploadSettle = TimeSpan.FromMinutes(15);
    private const int MaxBuckets = 100;
    private const int Page = 50;
    private const int ScanRows = 1000;
    private const long MaxFileBytes = 100 * 1024 * 1024;
    private const string OpenProject = "NOT EXISTS (SELECT 1 FROM platform_closed_file_projects WHERE project_id = ?)";
    private const string ClosedProject = "SELECT 1 FROM platform_closed_file_projects WHERE project_id = ?";
    private const int EraseBatch = 256;
    private const int EraseBytes = 2 * 1024 * 1024;
    private const int ConflictAttempts = 8;
    private const string SerializationConflict = "serialization_conflict";
    private const string FileBytes = "octet_length(project_id) + octet_length(bucket_id) + octet_length(id) + octet_length(organization_id) + octet_length(body) "
        + "+ octet_length(status) + 16";
    private static readonly string ClaimPendingFiles =
        "UPDATE platform_files SET status = 'deleted', delete_after = ? "
        + "WHERE project_id = ? AND bucket_id = ? AND status = 'pending' AND upload_expires_at <= ? "
        + $"AND id IN ({BudgetedFiles("(status = 'deleted' OR (status = 'pending' AND upload_expires_at <= ?))")}) AND {OpenProject}";
    private static readonly string ClaimedFiles = BudgetedFiles("status = 'deleted'");
    private static readonly PlatformForbidden Denied = new("the bucket rules deny this operation", "access_denied");
    private static readonly PlatformNotFound Missing = new("the bucket or file does not exist", "not_found");
    private static readonly PlatformUnavailable Erasing = new("the bucket's deleted files are still being erased; retry", "files_unavailable");
    private static readonly PlatformConflict LiveFiles =
        new("delete the bucket's files and let uploads in progress complete or expire before deleting the bucket", "bucket_not_empty");

    public async ValueTask<CreateBucketOutcome> CreateBucketAsync(
        FileScope scope,
        string? name,
        FileAccessRules? rules,
        CancellationToken cancellationToken)
    {
        if (!scope.Access.IsAdministrator)
        {
            return Denied;
        }

        if (!FileRoutes.IsName(name) || rules is null)
        {
            return new PlatformInvalidRequest("name must be a lowercase name of at most 63 characters; rules are required");
        }

        if (rules.Check() is { } invalid)
        {
            return invalid;
        }

        var now = timeProvider.GetUtcNow();
        var bucket = new FileBucket(Ulid.NewUlid(now).ToString(), name, options.Region, rules);
        var project = SqlValues.Text(scope.Project.ToString());
        var organization = SqlValues.Text(scope.Organization.ToString());
        var bucketId = SqlValues.Text(bucket.Id);
        var bucketName = SqlValues.Text(name);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement("SELECT 1 FROM platform_closed_file_projects WHERE project_id = ?", project),
            SqlValues.Statement("SELECT 1 FROM platform_buckets WHERE project_id = ? AND name = ?", project, bucketName),
            SqlValues.Statement(
                "INSERT INTO platform_buckets (project_id, id, organization_id, name, region, rules) SELECT ?, ?, ?, ?, ?, ? "
                + "WHERE (SELECT COUNT(*) FROM platform_buckets WHERE project_id = ?) < ? "
                + "AND NOT EXISTS (SELECT 1 FROM platform_buckets WHERE project_id = ? AND name = ?) "
                + "AND NOT EXISTS (SELECT 1 FROM platform_closed_file_projects WHERE project_id = ?)",
                project,
                bucketId,
                organization,
                bucketName,
                SqlValues.Text(bucket.Region),
                SqlValues.Text(JsonSerializer.Serialize(rules, FileStorageJsonContext.Default.FileAccessRules)),
                project,
                SqlValues.Number(MaxBuckets),
                project,
                bucketName,
                project),
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_bucket_regions (project_id, bucket_id, organization_id, region) "
                + "SELECT project_id, id, organization_id, region FROM platform_buckets WHERE project_id = ? AND id = ?",
                project,
                bucketId),
            FunctionService.FenceProject(project, organization, now),
        ];
        metrics.FileOperation("create_bucket");
        var outcome = await ExecuteStrongAsync(statements, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 => new PlatformNotFound("the project is closed", "project_not_found"),
            SqlExecuteResponse { Results: var results } when results[1].Rows.Count > 0 => new PlatformConflict("a bucket with that name already exists"),
            SqlExecuteResponse { Results: var results } when results[2].RowsAffected == 0 => new PlatformConflict($"a project may have at most {MaxBuckets} buckets"),
            SqlExecuteResponse => bucket,
            PlatformConflict conflict => conflict,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ListBucketsOutcome> ListBucketsAsync(FileScope scope, string? after, CancellationToken cancellationToken)
    {
        if (!scope.Access.IsAdministrator)
        {
            return Denied;
        }

        var project = SqlValues.Text(scope.Project.ToString());
        var statement = SqlValues.Statement(
            $"SELECT id, name, region, rules FROM platform_buckets WHERE project_id = ? AND id > ? AND {OpenProject} ORDER BY id LIMIT ?",
            project,
            SqlValues.Text(after ?? "0"),
            project,
            SqlValues.Number(Page + 1));
        var outcome = await store.ReadAsync(statement, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => Paged(results[0].RowsOf().Select(Bucket).OfType<FileBucket>().ToList()),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<SetBucketRulesOutcome> SetRulesAsync(
        FileScope scope,
        string bucket,
        FileAccessRules? rules,
        CancellationToken cancellationToken)
    {
        if (!scope.Access.IsAdministrator)
        {
            return Denied;
        }

        if (rules is null)
        {
            return new PlatformInvalidRequest("rules are required");
        }

        if (rules.Check() is { } invalid)
        {
            return invalid;
        }

        var outcome = await BucketAsync(scope, bucket, cancellationToken);
        return outcome switch
        {
            LocatedBucket { Bucket: var saved } when saved.Rules.Owner != rules.Owner => new PlatformConflict("the owner pointer is immutable"),
            LocatedBucket { Bucket: var saved } => await UpdateRulesAsync(saved),
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<SetBucketRulesOutcome> UpdateRulesAsync(FileBucket saved)
        {
            var outcome = await SaveRulesAsync(scope, saved with { Rules = rules }, cancellationToken);
            return outcome switch
            {
                FileBucket updated => updated,
                PlatformNotFound missing => missing,
                PlatformConflict conflict => conflict,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    public async ValueTask<DeleteBucketOutcome> DeleteBucketAsync(FileScope scope, string bucket, CancellationToken cancellationToken)
    {
        if (!scope.Access.IsAdministrator)
        {
            return Denied;
        }

        var outcome = await BucketAsync(scope, bucket, cancellationToken);
        return outcome switch
        {
            LocatedBucket { Bucket: var saved } => await DeleteAsync(saved),
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<DeleteBucketOutcome> DeleteAsync(FileBucket saved)
        {
            var outcome = await EraseBucketAsync(scope, saved, cancellationToken);
            return outcome switch
            {
                BucketDeleted deleted => deleted,
                PlatformNotFound missing => missing,
                PlatformConflict conflict => conflict,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    public async ValueTask<CreateUploadOutcome> CreateUploadAsync(
        FileScope scope,
        CreateFileUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Name is not { Length: > 0 and <= 255 } name || name.Any(char.IsControl)
            || request.ContentType is not { Length: > 0 and <= 128 } contentType || contentType.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character))
            || request.Size is < 1 or > MaxFileBytes || request.Metadata.ValueKind != JsonValueKind.Object
            || request.Metadata.GetRawText().Length > 16 * 1024 || request.Sha256 is not { } sha256 || !IsChecksum(sha256))
        {
            return new PlatformInvalidRequest("provide name, contentType, size (1-104857600), base64 SHA-256 and object metadata (at most 16 KiB)");
        }

        var now = timeProvider.GetUtcNow();
        var file = new FileObject(Ulid.NewUlid(now).ToString(), name, contentType, request.Size, sha256, request.Metadata.Clone(), "pending", now + UploadLifetime);
        var outcome = await BucketAsync(scope, request.Bucket, cancellationToken);
        return outcome switch
        {
            LocatedBucket saved when scope.Access.Grant.DatabaseRestricted || !saved.Bucket.Rules.CanCreate(scope.Access, file.Metadata) => Denied,
            LocatedBucket saved => objects.Upload(saved.Bucket.Region, S3FileObjectStore.Key(scope.Organization, scope.Project, saved.Bucket.Id, file.Id), file) switch
            {
                FileSignedUrl signed => await ReserveSignedAsync(saved, signed),
                PlatformUnavailable unavailable => unavailable,
            },
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<CreateUploadOutcome> ReserveSignedAsync(LocatedBucket saved, FileSignedUrl signed)
        {
            metrics.FileOperation("upload");
            var rulesJson = saved.RulesJson;
            for (var attempt = 1; attempt <= ConflictAttempts; attempt++)
            {
                var outcome = await ReserveAsync(scope, saved.Bucket, file, signed, rulesJson, cancellationToken);
                (string? RetryRules, CreateUploadOutcome? Result) step = outcome switch
                {
                    UploadCreated created => (null, created),
                    BucketRulesChanged changed => (changed.RulesJson, null),
                    PlatformNotFound missing => (null, missing),
                    PlatformUnavailable unavailable => (null, unavailable),
                };
                if (step.Result is { } result)
                {
                    return result;
                }

                if (step.RetryRules is not { } nextRulesJson
                    || TerraBaseAnswers.Parse(Encoding.UTF8.GetBytes(nextRulesJson), FileStorageJsonContext.Default.FileAccessRules) is not { } nextRules)
                {
                    return Missing;
                }

                if (scope.Access.Grant.DatabaseRestricted || !nextRules.CanCreate(scope.Access, file.Metadata))
                {
                    return Denied;
                }

                rulesJson = nextRulesJson;
            }

            return new PlatformUnavailable("the bucket rules kept changing during upload reservation; retry", "platform_state");
        }
    }

    public async ValueTask<CompleteUploadOutcome> CompleteUploadAsync(
        FileScope scope,
        string bucket,
        string file,
        CancellationToken cancellationToken)
    {
        var outcome = await FileAsync(scope, bucket, file, write: true, cancellationToken);
        return outcome switch
        {
            LocatedFile { File.Status: "ready" } located => await CompleteWithFenceAsync(located, alreadyReady: true),
            LocatedFile { File.Status: "deleted" } => Missing,
            LocatedFile located when located.File.UploadExpiresAt <= timeProvider.GetUtcNow() => new PlatformConflict("the upload reservation expired"),
            LocatedFile located => await CompleteVerifiedAsync(located),
            PlatformForbidden denied => denied,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<CompleteUploadOutcome> CompleteVerifiedAsync(LocatedFile located)
        {
            var verified = await objects.VerifyAsync(
                located.Bucket.Region,
                S3FileObjectStore.Key(scope.Organization, scope.Project, located.Bucket.Id, located.File.Id),
                located.File,
                cancellationToken);
            return verified switch
            {
                true => await CompleteWithFenceAsync(located, alreadyReady: false),
                false => new PlatformConflict("the uploaded file does not match the reserved size and checksum"),
                PlatformUnavailable unavailable => unavailable,
            };
        }

        async ValueTask<CompleteUploadOutcome> CompleteWithFenceAsync(LocatedFile located, bool alreadyReady)
        {
            var ready = alreadyReady ? located.File : located.File with { Status = "ready" };
            var rulesJson = located.RulesJson;
            for (var attempt = 1; attempt <= ConflictAttempts; attempt++)
            {
                var outcome = alreadyReady
                    ? await ConfirmReadyAsync(scope, located.Bucket, ready, rulesJson, cancellationToken)
                    : await SaveAsync(scope, located.Bucket, ready, rulesJson, cancellationToken);
                (string? RetryRules, CompleteUploadOutcome? Result) step = outcome switch
                {
                    FileObject saved => (null, saved),
                    BucketRulesChanged changed => (changed.RulesJson, null),
                    PlatformNotFound missing => (null, missing),
                    PlatformConflict conflict => (null, conflict),
                    PlatformUnavailable unavailable => (null, unavailable),
                };
                if (step.Result is { } result)
                {
                    return result;
                }

                if (step.RetryRules is not { } nextRulesJson
                    || TerraBaseAnswers.Parse(Encoding.UTF8.GetBytes(nextRulesJson), FileStorageJsonContext.Default.FileAccessRules) is not { } nextRules)
                {
                    return Missing;
                }

                if (!nextRules.Allows(scope.Access, located.File.Metadata, write: true))
                {
                    return Denied;
                }

                rulesJson = nextRulesJson;
            }

            return new PlatformUnavailable("the bucket rules kept changing during upload completion; retry", "platform_state");
        }
    }

    public async ValueTask<ListFilesOutcome> ListFilesAsync(
        FileScope scope,
        string bucket,
        string? after,
        CancellationToken cancellationToken)
    {
        var outcome = await BucketAsync(scope, bucket, cancellationToken);
        return outcome switch
        {
            LocatedBucket { Bucket: var saved } when scope.Access.Grant.DatabaseRestricted || (scope.Access.Grant.EndUser && !saved.Rules.Grants(scope.Access, write: false)) => Denied,
            LocatedBucket { Bucket: var saved } => await ListVisibleAsync(saved),
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<ListFilesOutcome> ListVisibleAsync(FileBucket saved)
        {
            var outcome = await ListReadyAsync(scope, saved, after, cancellationToken);
            return outcome switch
            {
                FileObjectPage page => page,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    public async ValueTask<CreateDownloadOutcome> DownloadAsync(
        FileScope scope,
        string bucket,
        string file,
        CancellationToken cancellationToken)
    {
        var outcome = await FileAsync(scope, bucket, file, write: false, cancellationToken);
        return outcome switch
        {
            LocatedFile { File.Status: "ready" } located =>
                objects.Download(located.Bucket.Region, S3FileObjectStore.Key(scope.Organization, scope.Project, located.Bucket.Id, located.File.Id), located.File) switch
                {
                    FileSignedUrl signed => Downloaded(signed),
                    PlatformUnavailable unavailable => unavailable,
                },
            LocatedFile { File.Status: "deleted" } => Missing,
            LocatedFile => new PlatformConflict("the upload is not complete"),
            PlatformForbidden denied => denied,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DeleteFileOutcome> DeleteFileAsync(
        FileScope scope,
        string bucket,
        string file,
        CancellationToken cancellationToken)
    {
        var outcome = await FileAsync(scope, bucket, file, write: true, cancellationToken);
        return outcome switch
        {
            LocatedFile located => await DeleteLocatedAsync(located),
            PlatformForbidden denied => denied,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<DeleteFileOutcome> DeleteLocatedAsync(LocatedFile located)
        {
            metrics.FileOperation("delete");
            var rulesJson = located.RulesJson;
            for (var attempt = 1; attempt <= ConflictAttempts; attempt++)
            {
                var outcome = await MarkDeletedAsync(scope, located.Bucket, located.File, rulesJson, cancellationToken);
                (string? RetryRules, DeleteFileOutcome? Result) step = outcome switch
                {
                    FileDeleted deleted => (null, deleted),
                    BucketRulesChanged changed => (changed.RulesJson, null),
                    PlatformNotFound missing => (null, missing),
                    PlatformUnavailable unavailable => (null, unavailable),
                };
                if (step.Result is { } result)
                {
                    return result;
                }

                if (step.RetryRules is not { } nextRulesJson
                    || TerraBaseAnswers.Parse(Encoding.UTF8.GetBytes(nextRulesJson), FileStorageJsonContext.Default.FileAccessRules) is not { } nextRules)
                {
                    return Missing;
                }

                if (!nextRules.Allows(scope.Access, located.File.Metadata, write: true))
                {
                    return Denied;
                }

                rulesJson = nextRulesJson;
            }

            return new PlatformUnavailable("the bucket rules kept changing during file deletion; retry", "platform_state");
        }
    }

    private FileSignedUrl Downloaded(FileSignedUrl signed)
    {
        metrics.FileOperation("download");
        return signed;
    }

    private readonly union SaveBucketRulesOutcome(FileBucket, PlatformNotFound, PlatformConflict, PlatformUnavailable);

    private async ValueTask<SaveBucketRulesOutcome> SaveRulesAsync(
        FileScope scope,
        FileBucket bucket,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var project = SqlValues.Text(scope.Project.ToString());
        var organization = SqlValues.Text(scope.Organization.ToString());
        var fence = SqlValues.Text(Ulid.NewUlid(now).ToString());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                $"UPDATE platform_buckets SET rules = ?, delete_fence = ? WHERE project_id = ? AND id = ? AND {OpenProject}",
                SqlValues.Text(JsonSerializer.Serialize(bucket.Rules, FileStorageJsonContext.Default.FileAccessRules)),
                fence,
                project,
                SqlValues.Text(bucket.Id),
                project),
            FunctionService.FenceProject(project, organization, now),
        ];
        var outcome = await ExecuteStrongAsync(statements, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].RowsAffected == 0 => Missing,
            SqlExecuteResponse => bucket,
            PlatformConflict conflict => conflict,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private readonly union EraseBucketOutcome(BucketDeleted, PlatformNotFound, PlatformConflict, PlatformUnavailable);

    private async ValueTask<EraseBucketOutcome> EraseBucketAsync(FileScope scope, FileBucket bucket, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var project = SqlValues.Text(scope.Project.ToString());
        var organization = SqlValues.Text(scope.Organization.ToString());
        var bucketId = SqlValues.Text(bucket.Id);
        var settled = (now - UploadSettle).UnixMs();
        var fence = SqlValues.Text(Ulid.NewUlid(now).ToString());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(ClosedProject, project),
            SqlValues.Statement(
                $"SELECT EXISTS (SELECT 1 FROM platform_files WHERE project_id = ? AND bucket_id = ? AND (status = 'ready' OR (status = 'pending' AND upload_expires_at > ?)) AND {OpenProject})",
                project,
                bucketId,
                SqlValues.Number(settled),
                project),
            SqlValues.Statement(
                $"UPDATE platform_buckets SET delete_fence = ? WHERE project_id = ? AND id = ? AND {OpenProject}",
                fence,
                project,
                bucketId,
                project),
            FunctionService.FenceProject(project, organization, now),
        ];
        var outcome = await ExecuteStrongAsync(statements, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 => Missing,
            SqlExecuteResponse { Results: var results } when results[2].RowsAffected == 0 => Missing,
            SqlExecuteResponse { Results: var results } when results[1].FirstRow() is { } row && row.Number(0) != 0 => LiveFiles,
            SqlExecuteResponse => await EraseFilesAsync(scope, bucket, settled, cancellationToken),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<PlatformStoreOutcome> ExecuteStrongAsync(
        IReadOnlyList<SqlHttpStatement> statements,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ConflictAttempts; attempt++)
        {
            var outcome = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
            var conflicted = outcome switch
            {
                PlatformConflict { Code: SerializationConflict } => true,
                SqlExecuteResponse => false,
                PlatformConflict => false,
                PlatformUnavailable => false,
            };
            if (!conflicted)
            {
                return outcome;
            }
        }

        return new PlatformUnavailable("the file operation kept conflicting with concurrent writes; retry", "platform_state");
    }

    private static string BudgetedFiles(string predicate) =>
        "SELECT id FROM (SELECT id, size, SUM(size) OVER (ORDER BY id ROWS UNBOUNDED PRECEDING) AS total FROM ("
        + $"SELECT id, {FileBytes} AS size FROM platform_files WHERE project_id = ? AND bucket_id = ? "
        + $"AND {predicate} AND {OpenProject} ORDER BY id LIMIT {EraseBatch})) "
        + $"WHERE total = size OR total <= {EraseBytes} ORDER BY id";

    private async ValueTask<EraseBucketOutcome> EraseFilesAsync(FileScope scope, FileBucket bucket, long settled, CancellationToken cancellationToken)
    {
        for (var round = 0; round <= ScanRows; round++)
        {
            var outcome = await EraseRoundAsync(scope, bucket, settled, cancellationToken);
            (bool More, EraseBucketOutcome Result) step = outcome switch
            {
                FilesErased => (true, Erasing),
                BucketDeleted deleted => (false, deleted),
                PlatformNotFound missing => (false, missing),
                PlatformConflict conflict => (false, conflict),
                PlatformUnavailable unavailable => (false, unavailable),
            };
            if (!step.More)
            {
                return step.Result;
            }
        }

        return Erasing;
    }

    private sealed record FilesErased;

    private readonly union EraseRoundOutcome(FilesErased, BucketDeleted, PlatformNotFound, PlatformConflict, PlatformUnavailable);

    private async ValueTask<EraseRoundOutcome> EraseRoundAsync(FileScope scope, FileBucket bucket, long settled, CancellationToken cancellationToken)
    {
        var project = SqlValues.Text(scope.Project.ToString());
        var organization = SqlValues.Text(scope.Organization.ToString());
        var bucketId = SqlValues.Text(bucket.Id);
        var cutoff = SqlValues.Number(settled);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(ClosedProject, project),
            SqlValues.Statement(ClaimPendingFiles, cutoff, project, bucketId, cutoff, project, bucketId, cutoff, project, project),
            SqlValues.Statement(ClaimedFiles, project, bucketId, project),
            FunctionService.FenceProject(project, organization, timeProvider.GetUtcNow()),
        ];
        var outcome = await ExecuteStrongAsync(statements, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 => Missing,
            SqlExecuteResponse { Results: var results } when results[2].Rows.Count == 0 => await DeleteEmptyBucketAsync(scope, bucket, cancellationToken),
            SqlExecuteResponse { Results: var results } => await EraseBatchAsync(scope, bucket, [.. results[2].RowsOf().Select(static row => row.Text(0))], settled, cancellationToken),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<EraseRoundOutcome> EraseBatchAsync(
        FileScope scope,
        FileBucket bucket,
        IReadOnlyList<string> files,
        long settled,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            var failedOutcome = await objects.DeleteAsync(bucket.Region, S3FileObjectStore.Key(scope.Organization, scope.Project, bucket.Id, file), cancellationToken);
            var failed = failedOutcome switch
            {
                FileObjectDeleted => false,
                PlatformUnavailable => true,
            };
            if (failed)
            {
                return Erasing;
            }
        }

        var project = SqlValues.Text(scope.Project.ToString());
        var organization = SqlValues.Text(scope.Organization.ToString());
        var bucketId = SqlValues.Text(bucket.Id);
        var placeholders = string.Join(", ", Enumerable.Repeat("?", files.Count));
        var fileIds = files.Select(SqlValues.Text).ToArray();
        var settleMs = (long)UploadSettle.TotalMilliseconds;
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(ClosedProject, project),
            SqlValues.Statement(
                "INSERT OR REPLACE INTO platform_file_purges (project_id, bucket_id, id, organization_id, region, purge_after) "
                + "SELECT project_id, bucket_id, id, organization_id, ?, MAX(upload_expires_at + ?, COALESCE(delete_after, 0)) FROM platform_files "
                + $"WHERE project_id = ? AND bucket_id = ? AND status = 'deleted' AND upload_expires_at > ? AND id IN ({placeholders}) AND {OpenProject}",
                [SqlValues.Text(bucket.Region), SqlValues.Number(settleMs), project, bucketId, SqlValues.Number(settled), .. fileIds, project]),
            SqlValues.Statement(
                $"DELETE FROM platform_files WHERE project_id = ? AND bucket_id = ? AND status = 'deleted' AND id IN ({placeholders}) AND {OpenProject}",
                [project, bucketId, .. fileIds, project]),
            FunctionService.FenceProject(project, organization, timeProvider.GetUtcNow()),
        ];
        var outcome = await ExecuteStrongAsync(statements, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 => Missing,
            SqlExecuteResponse => new FilesErased(),
            PlatformConflict conflict => conflict,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<EraseRoundOutcome> DeleteEmptyBucketAsync(FileScope scope, FileBucket bucket, CancellationToken cancellationToken)
    {
        var project = SqlValues.Text(scope.Project.ToString());
        var organization = SqlValues.Text(scope.Organization.ToString());
        var id = SqlValues.Text(bucket.Id);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(ClosedProject, project),
            SqlValues.Statement("SELECT 1 FROM platform_files WHERE project_id = ? AND bucket_id = ? LIMIT 1", project, id),
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_bucket_regions (project_id, bucket_id, organization_id, region) "
                + $"SELECT project_id, id, organization_id, region FROM platform_buckets WHERE project_id = ? AND id = ? AND {OpenProject} "
                + "AND NOT EXISTS (SELECT 1 FROM platform_files WHERE project_id = ? AND bucket_id = ?)",
                project,
                id,
                project,
                project,
                id),
            SqlValues.Statement(
                $"DELETE FROM platform_buckets WHERE project_id = ? AND id = ? AND {OpenProject} "
                + "AND NOT EXISTS (SELECT 1 FROM platform_files WHERE project_id = ? AND bucket_id = ?)",
                project,
                id,
                project,
                project,
                id),
            FunctionService.FenceProject(project, organization, timeProvider.GetUtcNow()),
        ];
        metrics.FileOperation("delete_bucket");
        var outcome = await ExecuteStrongAsync(statements, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 => Missing,
            SqlExecuteResponse { Results: var results } when results[3].RowsAffected > 0 => new BucketDeleted(),
            SqlExecuteResponse { Results: var results } when results[1].Rows.Count > 0 => LiveFiles,
            SqlExecuteResponse => Missing,
            PlatformConflict conflict => conflict,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private readonly union ReserveUploadOutcome(UploadCreated, BucketRulesChanged, PlatformNotFound, PlatformUnavailable);

    private async ValueTask<ReserveUploadOutcome> ReserveAsync(
        FileScope scope,
        FileBucket bucket,
        FileObject file,
        FileSignedUrl signed,
        string rulesJson,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var project = SqlValues.Text(scope.Project.ToString());
        var organization = SqlValues.Text(scope.Organization.ToString());
        var bucketId = SqlValues.Text(bucket.Id);
        var fence = SqlValues.Text(Ulid.NewUlid(now).ToString());
        var authorizedRules = SqlValues.Text(rulesJson);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(ClosedProject, project),
            SqlValues.Statement(
                $"UPDATE platform_buckets SET delete_fence = ? WHERE project_id = ? AND id = ? AND rules IS ? AND {OpenProject}",
                fence,
                project,
                bucketId,
                authorizedRules,
                project),
            SqlValues.Statement(
                "INSERT INTO platform_files (project_id, bucket_id, id, organization_id, body, status, upload_expires_at, delete_after) "
                + "SELECT ?, ?, ?, ?, ?, 'pending', ?, NULL "
                + $"WHERE EXISTS (SELECT 1 FROM platform_buckets WHERE project_id = ? AND id = ? AND delete_fence = ?) AND {OpenProject}",
                project,
                bucketId,
                SqlValues.Text(file.Id),
                organization,
                SqlValues.Text(JsonSerializer.Serialize(file, FileStorageJsonContext.Default.FileObject)),
                SqlValues.Number(file.UploadExpiresAt.UnixMs()),
                project,
                bucketId,
                fence,
                project),
            SqlValues.Statement(
                $"SELECT rules FROM platform_buckets WHERE project_id = ? AND id = ? AND {OpenProject}",
                project,
                bucketId,
                project),
            FunctionService.FenceProject(project, organization, now),
        ];
        var outcome = await ExecuteStrongAsync(statements, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 => Missing,
            SqlExecuteResponse { Results: var results } when results[1].RowsAffected == 0 =>
                results[3].FirstRow() is { } bucketRow ? new BucketRulesChanged(bucketRow.Text(0)) : Missing,
            SqlExecuteResponse { Results: var results } when results[2].RowsAffected == 0 => Missing,
            SqlExecuteResponse => new UploadCreated(signed),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private sealed record BucketRulesChanged(string RulesJson);

    private readonly union MarkFileDeletedOutcome(FileDeleted, BucketRulesChanged, PlatformNotFound, PlatformUnavailable);

    private async ValueTask<MarkFileDeletedOutcome> MarkDeletedAsync(
        FileScope scope,
        FileBucket bucket,
        FileObject file,
        string rulesJson,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var after = now.AddMinutes(6);
        if (file.UploadExpiresAt > after)
        {
            after = file.UploadExpiresAt.AddMinutes(1);
        }

        var project = SqlValues.Text(scope.Project.ToString());
        var organization = SqlValues.Text(scope.Organization.ToString());
        var bucketId = SqlValues.Text(bucket.Id);
        var fileId = SqlValues.Text(file.Id);
        var fence = SqlValues.Text(Ulid.NewUlid(now).ToString());
        var authorizedRules = SqlValues.Text(rulesJson);
        var deleted = file with { Status = "deleted", DeleteAfter = after };
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(ClosedProject, project),
            SqlValues.Statement(
                $"UPDATE platform_buckets SET delete_fence = ? WHERE project_id = ? AND id = ? AND rules IS ? AND {OpenProject}",
                fence,
                project,
                bucketId,
                authorizedRules,
                project),
            SqlValues.Statement(
                "UPDATE platform_files SET body = ?, status = 'deleted', delete_after = ? "
                + "WHERE project_id = ? AND bucket_id = ? AND id = ? AND status IN ('pending', 'ready') "
                + $"AND EXISTS (SELECT 1 FROM platform_buckets WHERE project_id = ? AND id = ? AND delete_fence = ?) AND {OpenProject}",
                SqlValues.Text(JsonSerializer.Serialize(deleted, FileStorageJsonContext.Default.FileObject)),
                SqlValues.Number(after.UnixMs()),
                project,
                bucketId,
                fileId,
                project,
                bucketId,
                fence,
                project),
            SqlValues.Statement(
                "SELECT status FROM platform_files WHERE project_id = ? AND bucket_id = ? AND id = ?",
                project,
                bucketId,
                fileId),
            SqlValues.Statement(
                $"SELECT rules FROM platform_buckets WHERE project_id = ? AND id = ? AND {OpenProject}",
                project,
                bucketId,
                project),
            FunctionService.FenceProject(project, organization, now),
        ];
        var outcome = await ExecuteStrongAsync(statements, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 => Missing,
            SqlExecuteResponse { Results: var results } when results[1].RowsAffected == 0 =>
                results[4].FirstRow() is { } bucketRow ? new BucketRulesChanged(bucketRow.Text(0)) : Missing,
            SqlExecuteResponse { Results: var results } when results[2].RowsAffected > 0 => new FileDeleted(),
            SqlExecuteResponse { Results: var results } when results[3].FirstRow() is not { } row || row.Text(0) == "deleted" => new FileDeleted(),
            SqlExecuteResponse => new PlatformUnavailable("the file could not be deleted; retry", "platform_state"),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private readonly union SaveFileOutcome(FileObject, BucketRulesChanged, PlatformNotFound, PlatformConflict, PlatformUnavailable);

    private async ValueTask<SaveFileOutcome> ConfirmReadyAsync(
        FileScope scope,
        FileBucket bucket,
        FileObject file,
        string rulesJson,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var project = SqlValues.Text(scope.Project.ToString());
        var organization = SqlValues.Text(scope.Organization.ToString());
        var bucketId = SqlValues.Text(bucket.Id);
        var fence = SqlValues.Text(Ulid.NewUlid(now).ToString());
        var authorizedRules = SqlValues.Text(rulesJson);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(ClosedProject, project),
            SqlValues.Statement(
                $"UPDATE platform_buckets SET delete_fence = ? WHERE project_id = ? AND id = ? AND rules IS ? AND {OpenProject}",
                fence,
                project,
                bucketId,
                authorizedRules,
                project),
            SqlValues.Statement(
                "SELECT 1 FROM platform_files WHERE project_id = ? AND bucket_id = ? AND id = ? AND status = 'ready' "
                + $"AND EXISTS (SELECT 1 FROM platform_buckets WHERE project_id = ? AND id = ? AND delete_fence = ?) AND {OpenProject}",
                project,
                bucketId,
                SqlValues.Text(file.Id),
                project,
                bucketId,
                fence,
                project),
            SqlValues.Statement(
                $"SELECT rules FROM platform_buckets WHERE project_id = ? AND id = ? AND {OpenProject}",
                project,
                bucketId,
                project),
            FunctionService.FenceProject(project, organization, now),
        ];
        var outcome = await ExecuteStrongAsync(statements, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 => Missing,
            SqlExecuteResponse { Results: var results } when results[1].RowsAffected == 0 =>
                results[3].FirstRow() is { } bucketRow ? new BucketRulesChanged(bucketRow.Text(0)) : Missing,
            SqlExecuteResponse { Results: var results } when results[2].Rows.Count == 0 => Missing,
            SqlExecuteResponse => file,
            PlatformConflict conflict => conflict,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<SaveFileOutcome> SaveAsync(
        FileScope scope,
        FileBucket bucket,
        FileObject file,
        string rulesJson,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var project = SqlValues.Text(scope.Project.ToString());
        var organization = SqlValues.Text(scope.Organization.ToString());
        var bucketId = SqlValues.Text(bucket.Id);
        var fence = SqlValues.Text(Ulid.NewUlid(now).ToString());
        var authorizedRules = SqlValues.Text(rulesJson);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(ClosedProject, project),
            SqlValues.Statement(
                $"UPDATE platform_buckets SET delete_fence = ? WHERE project_id = ? AND id = ? AND rules IS ? AND {OpenProject}",
                fence,
                project,
                bucketId,
                authorizedRules,
                project),
            SqlValues.Statement(
                "UPDATE platform_files SET body = ?, status = ?, delete_after = ? "
                + "WHERE project_id = ? AND bucket_id = ? AND id = ? AND status = 'pending' "
                + $"AND EXISTS (SELECT 1 FROM platform_buckets WHERE project_id = ? AND id = ? AND delete_fence = ?) AND {OpenProject}",
                SqlValues.Text(JsonSerializer.Serialize(file, FileStorageJsonContext.Default.FileObject)),
                SqlValues.Text(file.Status),
                SqlValues.OptionalNumber(file.DeleteAfter?.UnixMs()),
                project,
                bucketId,
                SqlValues.Text(file.Id),
                project,
                bucketId,
                fence,
                project),
            SqlValues.Statement(
                $"SELECT rules FROM platform_buckets WHERE project_id = ? AND id = ? AND {OpenProject}",
                project,
                bucketId,
                project),
            FunctionService.FenceProject(project, organization, now),
        ];
        var outcome = await ExecuteStrongAsync(statements, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 => Missing,
            SqlExecuteResponse { Results: var results } when results[1].RowsAffected == 0 =>
                results[3].FirstRow() is { } bucketRow ? new BucketRulesChanged(bucketRow.Text(0)) : Missing,
            SqlExecuteResponse { Results: var results } when results[2].RowsAffected == 0 => new PlatformConflict("the file changed concurrently; retry"),
            SqlExecuteResponse => file,
            PlatformConflict conflict => conflict,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private readonly union ListReadyFilesOutcome(FileObjectPage, PlatformNotFound, PlatformUnavailable);

    private async ValueTask<ListReadyFilesOutcome> ListReadyAsync(
        FileScope scope,
        FileBucket bucket,
        string? after,
        CancellationToken cancellationToken)
    {
        var project = SqlValues.Text(scope.Project.ToString());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(ClosedProject, project),
            SqlValues.Statement(
                $"SELECT body FROM platform_files WHERE project_id = ? AND bucket_id = ? AND status = 'ready' AND id > ? AND {OpenProject} ORDER BY id LIMIT ?",
                project,
                SqlValues.Text(bucket.Id),
                SqlValues.Text(after ?? "0"),
                project,
                SqlValues.Number(ScanRows)),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 => Missing,
            SqlExecuteResponse { Results: var results } => Visible(scope, bucket, results[1]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static FileObjectPage Visible(FileScope scope, FileBucket bucket, SqlHttpStatementResult rows)
    {
        var items = new List<FileObject>(Page);
        string? last = null;
        foreach (var row in rows.RowsOf())
        {
            if (Parse(row.Text(0)) is not { } file)
            {
                continue;
            }

            last = file.Id;
            if (!bucket.Rules.Allows(scope.Access, file.Metadata, write: false))
            {
                continue;
            }

            items.Add(file);
            if (items.Count == Page)
            {
                return new FileObjectPage(items, file.Id);
            }
        }

        return new FileObjectPage(items, rows.Rows.Count == ScanRows ? last : null);
    }

    private sealed record LocatedBucket(FileBucket Bucket, string RulesJson);

    private readonly union ReadBucketOutcome(LocatedBucket, PlatformNotFound, PlatformUnavailable);

    private async ValueTask<ReadBucketOutcome> BucketAsync(FileScope scope, string bucket, CancellationToken cancellationToken)
    {
        if (!Ulid.TryParse(bucket, out _))
        {
            return Missing;
        }

        var project = SqlValues.Text(scope.Project.ToString());
        var statement = SqlValues.Statement(
            $"SELECT id, name, region, rules FROM platform_buckets WHERE project_id = ? AND id = ? AND {OpenProject}",
            project,
            SqlValues.Text(bucket),
            project);
        var outcome = await store.ReadAsync(statement, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].FirstRow() is { } row && Bucket(row) is { } saved ? new LocatedBucket(saved, row.Text(3)) : Missing,
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private readonly union LocateFileOutcome(LocatedFile, PlatformForbidden, PlatformNotFound, PlatformUnavailable);

    private async ValueTask<LocateFileOutcome> FileAsync(
        FileScope scope,
        string bucket,
        string file,
        bool write,
        CancellationToken cancellationToken)
    {
        if (!Ulid.TryParse(bucket, out _) || !Ulid.TryParse(file, out _))
        {
            return Missing;
        }

        var project = SqlValues.Text(scope.Project.ToString());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement($"SELECT id, name, region, rules FROM platform_buckets WHERE project_id = ? AND id = ? AND {OpenProject}", project, SqlValues.Text(bucket), project),
            SqlValues.Statement(
                $"SELECT body, status FROM platform_files WHERE project_id = ? AND bucket_id = ? AND id = ? AND {OpenProject}",
                project,
                SqlValues.Text(bucket),
                SqlValues.Text(file),
                project),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => Located(scope, results, write),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static LocateFileOutcome Located(
        FileScope scope,
        IReadOnlyList<SqlHttpStatementResult> results,
        bool write)
    {
        if (results[0].FirstRow() is not { } bucketRow || Bucket(bucketRow) is not { } bucket
            || results[1].FirstRow() is not { } fileRow || Parse(fileRow.Text(0)) is not { } parsed)
        {
            return Missing;
        }

        var file = parsed with { Status = fileRow.Text(1) };
        return bucket.Rules.Allows(scope.Access, file.Metadata, write) ? new LocatedFile(bucket, file, bucketRow.Text(3)) : Denied;
    }

    private static FileBucketPage Paged(List<FileBucket> buckets) =>
        buckets.Count > Page ? new FileBucketPage(buckets[..Page], buckets[Page - 1].Id) : new FileBucketPage(buckets, null);

    private static FileBucket? Bucket(SqlRow row) =>
        TerraBaseAnswers.Parse(Encoding.UTF8.GetBytes(row.Text(3)), FileStorageJsonContext.Default.FileAccessRules) is { } rules
            ? new FileBucket(row.Text(0), row.Text(1), row.Text(2), rules)
            : null;

    private static FileObject? Parse(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return bytes.Length == 0 ? null : FileJson.Read(bytes);
    }

    private static bool IsChecksum(string checksum)
    {
        Span<byte> bytes = stackalloc byte[32];
        return checksum.Length == 44 && Convert.TryFromBase64String(checksum, bytes, out var written) && written == 32;
    }
}

public sealed record LocatedFile(FileBucket Bucket, FileObject File, string RulesJson);

public static class FileJson
{
    public static FileObject? Read(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, FileStorageJsonContext.Default.FileObject);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
