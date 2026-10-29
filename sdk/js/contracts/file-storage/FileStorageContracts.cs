using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.FileStorage;

/// <summary>A project file bucket with the same end-user policy as document collections.</summary>
public sealed record FileBucket(string Id, string Name, string Region, CollectionAccessRules Rules);
/// <summary>A new bucket. Rules deny end users unless explicitly granted.</summary>
public sealed record CreateFileBucketRequest(string? Name, CollectionAccessRules? Rules);
/// <summary>A replacement bucket policy.</summary>
public sealed record FileBucketRulesRequest(CollectionAccessRules? Rules);
/// <summary>An immutable file upload reservation. SHA-256 is base64, not hex.</summary>
public sealed record CreateFileUploadRequest(string? Name, string? ContentType, long Size, string? Sha256, JsonElement Metadata);
/// <summary>A file's customer-visible metadata. IDs and ownership cannot be overwritten.</summary>
public sealed record FileObject(string Id, string Name, string ContentType, long Size, string Sha256, JsonElement Metadata, string Status, DateTimeOffset UploadExpiresAt, DateTimeOffset? DeleteAfter = null);
/// <summary>A short-lived bearer capability. Send every returned header exactly; never log the URL.</summary>
public sealed record FileSignedUrl(string Id, string Url, DateTimeOffset ExpiresAt, Dictionary<string, string> Headers);
/// <summary>A bounded page in ID order.</summary>
public sealed record FileBucketPage(IReadOnlyList<FileBucket> Items, string? Next);
/// <summary>A bounded page filtered by read permission before pagination.</summary>
public sealed record FileObjectPage(IReadOnlyList<FileObject> Items, string? Next);
/// <summary>A file operation or its typed failure.</summary>
public readonly union FileStorageResult(FileBucket, FileBucketPage, FileObject, FileObjectPage, FileSignedUrl, Done, Validation, Forbidden, NotFound, Conflict, QuotaExceeded, Unavailable);
/// <summary>The project-scoped file API.</summary>
public interface IFileStorage
{
    /// <summary>Creates a bucket; only project credentials can manage policies.</summary>
    ValueTask<FileStorageResult> CreateBucketAsync(CreateFileBucketRequest request, CancellationToken cancellationToken);
    /// <summary>Lists project buckets.</summary>
    ValueTask<FileStorageResult> ListBucketsAsync(string? after, CancellationToken cancellationToken);
    /// <summary>Replaces a bucket's rules.</summary>
    ValueTask<FileStorageResult> SetRulesAsync(string bucket, FileBucketRulesRequest request, CancellationToken cancellationToken);
    /// <summary>Deletes an empty bucket.</summary>
    ValueTask<FileStorageResult> DeleteBucketAsync(string bucket, CancellationToken cancellationToken);
    /// <summary>Reserves plan storage and returns an immutable, length/checksum-bound upload URL.</summary>
    ValueTask<FileStorageResult> CreateUploadAsync(string bucket, CreateFileUploadRequest request, CancellationToken cancellationToken);
    /// <summary>Verifies the object before publishing it to readers.</summary>
    ValueTask<FileStorageResult> CompleteUploadAsync(string bucket, string file, CancellationToken cancellationToken);
    /// <summary>Lists readable, completed files.</summary>
    ValueTask<FileStorageResult> ListFilesAsync(string bucket, string? after, CancellationToken cancellationToken);
    /// <summary>Authorizes a short-lived download.</summary>
    ValueTask<FileStorageResult> DownloadAsync(string bucket, string file, CancellationToken cancellationToken);
    /// <summary>Revokes new downloads and queues physical deletion after outstanding uploads expire.</summary>
    ValueTask<FileStorageResult> DeleteFileAsync(string bucket, string file, CancellationToken cancellationToken);
}
/// <summary>Object-store boundary. Production uses R2; integration tests can use Tigris.</summary>
public interface IFileObjectStore
{
    /// <summary>Signs a checksum- and length-bound immutable upload.</summary>
    ValueTask<FileSignedUrl> UploadAsync(string region, string key, FileObject file, CancellationToken cancellationToken);
    /// <summary>Signs a download as an attachment, never active content on the application origin.</summary>
    ValueTask<FileSignedUrl> DownloadAsync(string region, string key, FileObject file, CancellationToken cancellationToken);
    /// <summary>Verifies stored length and checksum against the reservation.</summary>
    ValueTask<bool> VerifyAsync(string region, string key, FileObject file, CancellationToken cancellationToken);
    /// <summary>Idempotently deletes an object.</summary>
    ValueTask DeleteAsync(string region, string key, CancellationToken cancellationToken);
}
