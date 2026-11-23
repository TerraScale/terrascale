using System.Text;

namespace TerraScale.Platform.Features.FileStorage;

public sealed class FileStorageService(PlatformStore store, S3FileObjectStore objects, PlatformOptions options, PlatformMetrics metrics, TimeProvider timeProvider)
{
    public static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(5);
    private const int MaxBuckets = 100;
    private const int Page = 50;
    private const int ScanRows = 1000;
    private const long MaxFileBytes = 100 * 1024 * 1024;
    private static readonly Forbidden Denied = new("the bucket rules deny this operation", "access_denied");
    private static readonly NotFound Missing = new("the bucket or file does not exist", "not_found");
    private static readonly Conflict LiveFiles =
        new("delete the bucket's files and let uploads in progress complete or expire before deleting the bucket", "bucket_not_empty");

    public async ValueTask<Result<FileBucket, Validation, Forbidden, NotFound, Conflict, Unavailable>> CreateBucketAsync(
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
            return new Validation("name must be a lowercase name of at most 63 characters; rules are required");
        }

        if (rules.Check() is { } invalid)
        {
            return invalid;
        }

        var bucket = new FileBucket(Ulid.NewUlid(timeProvider.GetUtcNow()).ToString(), name, options.Region, rules);
        var project = SqlValues.Text(scope.Project.ToString());
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
                SqlValues.Text(bucket.Id),
                SqlValues.Text(scope.Organization.ToString()),
                bucketName,
                SqlValues.Text(bucket.Region),
                SqlValues.Text(JsonSerializer.Serialize(rules, FileStorageJsonContext.Default.FileAccessRules)),
                project,
                SqlValues.Number(MaxBuckets),
                project,
                bucketName,
                project),
        ];
        metrics.FileOperation("create_bucket");
        return await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results when results[0].Rows.Count > 0 => new NotFound("the project is closed", "project_not_found"),
            IReadOnlyList<SqlHttpStatementResult> results when results[1].Rows.Count > 0 => new Conflict("a bucket with that name already exists"),
            IReadOnlyList<SqlHttpStatementResult> results when results[2].RowsAffected == 0 => new Conflict($"a project may have at most {MaxBuckets} buckets"),
            IReadOnlyList<SqlHttpStatementResult> => bucket,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<FileBucketPage, Forbidden, Unavailable>> ListBucketsAsync(FileScope scope, string? after, CancellationToken cancellationToken)
    {
        if (!scope.Access.IsAdministrator)
        {
            return Denied;
        }

        var statement = SqlValues.Statement(
            "SELECT id, name, region, rules FROM platform_buckets WHERE project_id = ? AND id > ? ORDER BY id LIMIT ?",
            SqlValues.Text(scope.Project.ToString()),
            SqlValues.Text(after ?? "0"),
            SqlValues.Number(Page + 1));
        return await store.ReadAsync(statement, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => Paged(results[0].RowsOf().Select(Bucket).OfType<FileBucket>().ToList()),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<FileBucket, Validation, Forbidden, NotFound, Conflict, Unavailable>> SetRulesAsync(
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
            return new Validation("rules are required");
        }

        if (rules.Check() is { } invalid)
        {
            return invalid;
        }

        return await BucketAsync(scope, bucket, cancellationToken) switch
        {
            FileBucket saved when saved.Rules.Owner != rules.Owner => new Conflict("the owner pointer is immutable"),
            FileBucket saved => await SaveRulesAsync(scope, saved with { Rules = rules }, cancellationToken),
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<Done, Forbidden, NotFound, Conflict, Unavailable>> DeleteBucketAsync(FileScope scope, string bucket, CancellationToken cancellationToken)
    {
        if (!scope.Access.IsAdministrator)
        {
            return Denied;
        }

        return await BucketAsync(scope, bucket, cancellationToken) switch
        {
            FileBucket saved => await EraseBucketAsync(scope, saved, cancellationToken),
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<UploadCreated, Validation, Forbidden, NotFound, Unavailable>> CreateUploadAsync(
        FileScope scope,
        CreateFileUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Name is not { Length: > 0 and <= 255 } name || name.Any(char.IsControl)
            || request.ContentType is not { Length: > 0 and <= 128 } contentType || contentType.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character))
            || request.Size is < 1 or > MaxFileBytes || request.Metadata.ValueKind != JsonValueKind.Object
            || request.Metadata.GetRawText().Length > 16 * 1024 || request.Sha256 is not { } sha256 || !IsChecksum(sha256))
        {
            return new Validation("provide name, contentType, size (1-104857600), base64 SHA-256 and object metadata (at most 16 KiB)");
        }

        var now = timeProvider.GetUtcNow();
        var file = new FileObject(Ulid.NewUlid(now).ToString(), name, contentType, request.Size, sha256, request.Metadata.Clone(), "pending", now + UploadLifetime);
        return await BucketAsync(scope, request.Bucket, cancellationToken) switch
        {
            FileBucket saved when scope.Access.Grant.DatabaseRestricted || !saved.Rules.CanCreate(scope.Access, file.Metadata) => Denied,
            FileBucket saved => objects.Upload(saved.Region, S3FileObjectStore.Key(scope.Organization, scope.Project, saved.Id, file.Id), file) switch
            {
                FileSignedUrl signed => await ReserveAsync(scope, saved, file, signed, cancellationToken),
                Unavailable unavailable => unavailable,
            },
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<FileObject, Forbidden, NotFound, Conflict, Unavailable>> CompleteUploadAsync(
        FileScope scope,
        string bucket,
        string file,
        CancellationToken cancellationToken) =>
        await FileAsync(scope, bucket, file, write: true, cancellationToken) switch
        {
            LocatedFile { File.Status: "ready" } located => located.File,
            LocatedFile { File.Status: "deleted" } => Missing,
            LocatedFile located when located.File.UploadExpiresAt <= timeProvider.GetUtcNow() => new Conflict("the upload reservation expired"),
            LocatedFile located => await VerifyAsync(scope, located.Bucket, located.File, cancellationToken),
            Forbidden denied => denied,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<FileObjectPage, Forbidden, NotFound, Unavailable>> ListFilesAsync(
        FileScope scope,
        string bucket,
        string? after,
        CancellationToken cancellationToken) =>
        await BucketAsync(scope, bucket, cancellationToken) switch
        {
            FileBucket saved when scope.Access.Grant.DatabaseRestricted || (scope.Access.Grant.EndUser && !saved.Rules.Grants(scope.Access, write: false)) => Denied,
            FileBucket saved => await ListReadyAsync(scope, saved, after, cancellationToken),
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<FileSignedUrl, Forbidden, NotFound, Conflict, Unavailable>> DownloadAsync(
        FileScope scope,
        string bucket,
        string file,
        CancellationToken cancellationToken) =>
        await FileAsync(scope, bucket, file, write: false, cancellationToken) switch
        {
            LocatedFile { File.Status: "ready" } located =>
                objects.Download(located.Bucket.Region, S3FileObjectStore.Key(scope.Organization, scope.Project, located.Bucket.Id, located.File.Id), located.File) switch
                {
                    FileSignedUrl signed => Downloaded(signed),
                    Unavailable unavailable => unavailable,
                },
            LocatedFile { File.Status: "deleted" } => Missing,
            LocatedFile => new Conflict("the upload is not complete"),
            Forbidden denied => denied,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<Done, Forbidden, NotFound, Unavailable>> DeleteFileAsync(
        FileScope scope,
        string bucket,
        string file,
        CancellationToken cancellationToken) =>
        await FileAsync(scope, bucket, file, write: true, cancellationToken) switch
        {
            LocatedFile { File.Status: "deleted" } => new Done(),
            LocatedFile located => await MarkDeletedAsync(scope, located.Bucket, located.File, cancellationToken),
            Forbidden denied => denied,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    private FileSignedUrl Downloaded(FileSignedUrl signed)
    {
        metrics.FileOperation("download");
        return signed;
    }

    private async ValueTask<Result<FileBucket, Validation, Forbidden, NotFound, Conflict, Unavailable>> SaveRulesAsync(
        FileScope scope,
        FileBucket bucket,
        CancellationToken cancellationToken)
    {
        var statement = SqlValues.Statement(
            "UPDATE platform_buckets SET rules = ? WHERE project_id = ? AND id = ?",
            SqlValues.Text(JsonSerializer.Serialize(bucket.Rules, FileStorageJsonContext.Default.FileAccessRules)),
            SqlValues.Text(scope.Project.ToString()),
            SqlValues.Text(bucket.Id));
        return await store.ExecuteAsync([statement], SqlContext.Fast, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results when results[0].RowsAffected == 0 => Missing,
            IReadOnlyList<SqlHttpStatementResult> => bucket,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<Done, Forbidden, NotFound, Conflict, Unavailable>> EraseBucketAsync(FileScope scope, FileBucket bucket, CancellationToken cancellationToken)
    {
        var statement = SqlValues.Statement(
            "SELECT id, status, upload_expires_at FROM platform_files WHERE project_id = ? AND bucket_id = ? LIMIT ?",
            SqlValues.Text(scope.Project.ToString()),
            SqlValues.Text(bucket.Id),
            SqlValues.Number(ScanRows + 1));
        return await store.ReadAsync(statement, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => await EraseFilesAsync(scope, bucket, results[0], cancellationToken),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<Done, Forbidden, NotFound, Conflict, Unavailable>> EraseFilesAsync(
        FileScope scope,
        FileBucket bucket,
        SqlHttpStatementResult rows,
        CancellationToken cancellationToken)
    {
        var files = rows.RowsOf().Select(static row => (Id: row.Text(0), Status: row.Text(1), Expires: row.Number(2))).ToList();
        var now = timeProvider.GetUtcNow().UnixMs();
        if (files.Count > ScanRows || files.Exists(file => file.Status == "ready" || (file.Status == "pending" && file.Expires > now)))
        {
            return LiveFiles;
        }

        foreach (var file in files)
        {
            var failed = await objects.DeleteAsync(bucket.Region, S3FileObjectStore.Key(scope.Organization, scope.Project, bucket.Id, file.Id), cancellationToken) switch
            {
                Done => false,
                Unavailable => true,
            };
            if (failed)
            {
                return new Unavailable("the bucket's deleted files are still being erased; retry", "files_unavailable");
            }
        }

        var project = SqlValues.Text(scope.Project.ToString());
        var id = SqlValues.Text(bucket.Id);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement("DELETE FROM platform_files WHERE project_id = ? AND bucket_id = ?", project, id),
            SqlValues.Statement("DELETE FROM platform_buckets WHERE project_id = ? AND id = ?", project, id),
        ];
        metrics.FileOperation("delete_bucket");
        return await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results when results[1].RowsAffected == 0 => Missing,
            IReadOnlyList<SqlHttpStatementResult> => new Done(),
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<UploadCreated, Validation, Forbidden, NotFound, Unavailable>> ReserveAsync(
        FileScope scope,
        FileBucket bucket,
        FileObject file,
        FileSignedUrl signed,
        CancellationToken cancellationToken)
    {
        var statement = SqlValues.Statement(
            "INSERT INTO platform_files (project_id, bucket_id, id, organization_id, body, status, upload_expires_at, delete_after) "
            + "SELECT ?, ?, ?, ?, ?, 'pending', ?, NULL WHERE EXISTS (SELECT 1 FROM platform_buckets WHERE project_id = ? AND id = ?)",
            SqlValues.Text(scope.Project.ToString()),
            SqlValues.Text(bucket.Id),
            SqlValues.Text(file.Id),
            SqlValues.Text(scope.Organization.ToString()),
            SqlValues.Text(JsonSerializer.Serialize(file, FileStorageJsonContext.Default.FileObject)),
            SqlValues.Number(file.UploadExpiresAt.UnixMs()),
            SqlValues.Text(scope.Project.ToString()),
            SqlValues.Text(bucket.Id));
        metrics.FileOperation("upload");
        return await store.ExecuteAsync([statement], SqlContext.Fast, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results when results[0].RowsAffected == 0 => Missing,
            IReadOnlyList<SqlHttpStatementResult> => new UploadCreated(signed),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<FileObject, Forbidden, NotFound, Conflict, Unavailable>> VerifyAsync(
        FileScope scope,
        FileBucket bucket,
        FileObject file,
        CancellationToken cancellationToken) =>
        await objects.VerifyAsync(bucket.Region, S3FileObjectStore.Key(scope.Organization, scope.Project, bucket.Id, file.Id), file, cancellationToken) switch
        {
            true => await SaveAsync(scope, bucket, file with { Status = "ready" }, "pending", cancellationToken),
            false => new Conflict("the uploaded file does not match the reserved size and checksum"),
            Unavailable unavailable => unavailable,
        };

    private async ValueTask<Result<Done, Forbidden, NotFound, Unavailable>> MarkDeletedAsync(
        FileScope scope,
        FileBucket bucket,
        FileObject file,
        CancellationToken cancellationToken)
    {
        var after = timeProvider.GetUtcNow().AddMinutes(6);
        if (file.UploadExpiresAt > after)
        {
            after = file.UploadExpiresAt.AddMinutes(1);
        }

        metrics.FileOperation("delete");
        return await SaveAsync(scope, bucket, file with { Status = "deleted", DeleteAfter = after }, file.Status, cancellationToken) switch
        {
            FileObject => new Done(),
            Forbidden denied => denied,
            NotFound missing => missing,
            Conflict => new Done(),
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<FileObject, Forbidden, NotFound, Conflict, Unavailable>> SaveAsync(
        FileScope scope,
        FileBucket bucket,
        FileObject file,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        var statement = SqlValues.Statement(
            "UPDATE platform_files SET body = ?, status = ?, delete_after = ? WHERE project_id = ? AND bucket_id = ? AND id = ? AND status = ?",
            SqlValues.Text(JsonSerializer.Serialize(file, FileStorageJsonContext.Default.FileObject)),
            SqlValues.Text(file.Status),
            SqlValues.OptionalNumber(file.DeleteAfter?.UnixMs()),
            SqlValues.Text(scope.Project.ToString()),
            SqlValues.Text(bucket.Id),
            SqlValues.Text(file.Id),
            SqlValues.Text(expectedStatus));
        return await store.ExecuteAsync([statement], SqlContext.Fast, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results when results[0].RowsAffected == 0 => new Conflict("the file changed concurrently; retry"),
            IReadOnlyList<SqlHttpStatementResult> => file,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<FileObjectPage, Forbidden, NotFound, Unavailable>> ListReadyAsync(
        FileScope scope,
        FileBucket bucket,
        string? after,
        CancellationToken cancellationToken)
    {
        var statement = SqlValues.Statement(
            "SELECT body FROM platform_files WHERE project_id = ? AND bucket_id = ? AND status = 'ready' AND id > ? ORDER BY id LIMIT ?",
            SqlValues.Text(scope.Project.ToString()),
            SqlValues.Text(bucket.Id),
            SqlValues.Text(after ?? "0"),
            SqlValues.Number(ScanRows));
        return await store.ReadAsync(statement, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => Visible(scope, bucket, results[0]),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
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

    private async ValueTask<Result<FileBucket, NotFound, Unavailable>> BucketAsync(FileScope scope, string bucket, CancellationToken cancellationToken)
    {
        if (!Ulid.TryParse(bucket, out _))
        {
            return Missing;
        }

        var statement = SqlValues.Statement(
            "SELECT id, name, region, rules FROM platform_buckets WHERE project_id = ? AND id = ?",
            SqlValues.Text(scope.Project.ToString()),
            SqlValues.Text(bucket));
        return await store.ReadAsync(statement, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => results[0].FirstRow() is { } row && Bucket(row) is { } saved ? saved : Missing,
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<LocatedFile, Forbidden, NotFound, Unavailable>> FileAsync(
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
            SqlValues.Statement("SELECT id, name, region, rules FROM platform_buckets WHERE project_id = ? AND id = ?", project, SqlValues.Text(bucket)),
            SqlValues.Statement("SELECT body FROM platform_files WHERE project_id = ? AND bucket_id = ? AND id = ?", project, SqlValues.Text(bucket), SqlValues.Text(file)),
        ];
        return await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => Located(scope, results, write),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };
    }

    private static Result<LocatedFile, Forbidden, NotFound, Unavailable> Located(
        FileScope scope,
        IReadOnlyList<SqlHttpStatementResult> results,
        bool write)
    {
        if (results[0].FirstRow() is not { } bucketRow || Bucket(bucketRow) is not { } bucket
            || results[1].FirstRow() is not { } fileRow || Parse(fileRow.Text(0)) is not { } file)
        {
            return Missing;
        }

        return bucket.Rules.Allows(scope.Access, file.Metadata, write) ? new LocatedFile(bucket, file) : Denied;
    }

    private static Result<FileBucketPage, Forbidden, Unavailable> Paged(List<FileBucket> buckets) =>
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

public sealed record LocatedFile(FileBucket Bucket, FileObject File);

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
