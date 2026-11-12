using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.FileStorage;

public sealed record FileBucket(string Id, string Name, string Region, FileAccessRules Rules);
public sealed record CreateFileBucketRequest(string? Name, FileAccessRules? Rules);
public sealed record FileBucketRulesRequest(FileAccessRules? Rules);
public sealed record CreateFileUploadRequest(string? Name, string? ContentType, long Size, string? Sha256, JsonElement Metadata);
public sealed record FileObject(string Id, string Name, string ContentType, long Size, string Sha256, JsonElement Metadata, string Status, DateTimeOffset UploadExpiresAt, DateTimeOffset? DeleteAfter = null);
public sealed record FileSignedUrl(string Id, string Url, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers);
public sealed record FileBucketPage(IReadOnlyList<FileBucket> Items, string? Next);
public sealed record FileObjectPage(IReadOnlyList<FileObject> Items, string? Next);
public sealed record FileObjectListing(IReadOnlyList<string> Keys, bool Truncated);
public readonly union FileStorageResult(FileBucket, FileBucketPage, FileObject, FileObjectPage, FileSignedUrl, Done, Validation, Forbidden, NotFound, Conflict, QuotaExceeded, Unavailable);
public interface IFileStorage
{
    ValueTask<FileStorageResult> CreateBucketAsync(CreateFileBucketRequest request, CancellationToken cancellationToken);
    ValueTask<FileStorageResult> ListBucketsAsync(string? after, CancellationToken cancellationToken);
    ValueTask<FileStorageResult> SetRulesAsync(string bucket, FileBucketRulesRequest request, CancellationToken cancellationToken);
    ValueTask<FileStorageResult> DeleteBucketAsync(string bucket, CancellationToken cancellationToken);
    ValueTask<FileStorageResult> CreateUploadAsync(string bucket, CreateFileUploadRequest request, CancellationToken cancellationToken);
    ValueTask<FileStorageResult> CompleteUploadAsync(string bucket, string file, CancellationToken cancellationToken);
    ValueTask<FileStorageResult> ListFilesAsync(string bucket, string? after, CancellationToken cancellationToken);
    ValueTask<FileStorageResult> DownloadAsync(string bucket, string file, CancellationToken cancellationToken);
    ValueTask<FileStorageResult> DeleteFileAsync(string bucket, string file, CancellationToken cancellationToken);
}
public interface IFileObjectStore
{
    bool Configured { get; }
    ValueTask<FileSignedUrl> UploadAsync(string region, string key, FileObject file, CancellationToken cancellationToken);
    ValueTask<FileSignedUrl> DownloadAsync(string region, string key, FileObject file, CancellationToken cancellationToken);
    ValueTask<bool> VerifyAsync(string region, string key, FileObject file, CancellationToken cancellationToken);
    ValueTask DeleteAsync(string region, string key, CancellationToken cancellationToken);
    ValueTask<FileObjectListing> ListAsync(string region, string prefix, CancellationToken cancellationToken);
}
