using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Owner, public and trusted-role grants for one file operation.</summary>
public sealed record TerraBaseFilePolicy(bool Owner, bool Public, IReadOnlyList<string> Roles);
/// <summary>Document-style rules evaluated against immutable file metadata. Owner is a JSON Pointer.</summary>
public sealed record TerraBaseFileRules(string? Owner, TerraBaseFilePolicy Read, TerraBaseFilePolicy Write);
/// <summary>A project's logical file bucket. Region identifies its object backing.</summary>
public sealed record TerraBaseFileBucket(string Id, string Name, string Region, TerraBaseFileRules Rules);
/// <summary>A file's immutable metadata. Pending files cannot be downloaded.</summary>
public sealed record TerraBaseStoredFile(string Id, string Name, string ContentType, long Size, string Sha256,
    JsonElement Metadata, string Status, DateTimeOffset UploadExpiresAt);
/// <summary>A private short-lived upload or download capability. Never log Url or Headers.</summary>
public sealed record TerraBaseFileUrl(string Id, string Url, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers)
{
    /// <inheritdoc />
    public override string ToString() => $"File capability for {Id} (URL redacted)";
}
/// <summary>An immutable upload reservation; SHA-256 is base64 and size is 1–104857600 bytes.</summary>
public sealed record TerraBaseFileUpload(string Name, string ContentType, long Size, string Sha256, JsonElement Metadata);
internal sealed record TerraBaseFileBucketRequest(string Name, TerraBaseFileRules Rules);
internal sealed record TerraBaseFileRulesRequest(TerraBaseFileRules Rules);

public sealed partial class TerraBaseProjectClient
{
    /// <summary>Creates a bucket. Requires project credentials and documents:write. Sent once.</summary>
    public async Task<TerraBaseFileBucket> CreateFileBucketAsync(string name, TerraBaseFileRules rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var body = TerraBaseConnection.Json(new TerraBaseFileBucketRequest(name, rules), Json.TerraBaseFileBucketRequest);
        return (await connection.SendAsync(HttpMethod.Post, path + "/buckets", body, Json.TerraBaseFileBucket, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Lists up to 50 buckets. Requires project credentials and documents:read.</summary>
    public async Task<TerraBasePage<TerraBaseFileBucket>> ListFileBucketsAsync(string? after = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/buckets" + FileAfter(after), null,
            Json.TerraBasePageTerraBaseFileBucket, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Replaces read/write grants; the owner pointer is immutable. Requires project credentials.</summary>
    public async Task<TerraBaseFileBucket> SetFileBucketRulesAsync(string bucketId, TerraBaseFileRules rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var body = TerraBaseConnection.Json(new TerraBaseFileRulesRequest(rules), Json.TerraBaseFileRulesRequest);
        return (await connection.SendAsync(HttpMethod.Put, FileBucketPath(bucketId) + "/rules", body, Json.TerraBaseFileBucket, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Deletes an empty bucket. Pending cleanup returns 409. Requires project credentials; sent once.</summary>
    public Task DeleteFileBucketAsync(string bucketId, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, FileBucketPath(bucketId), null, null, cancellationToken);

    /// <summary>Reserves shared organization storage and signs a five-minute immutable PUT. Send every returned header with the exact bytes. Sent once.</summary>
    public async Task<TerraBaseFileUrl> CreateFileUploadAsync(string bucketId, TerraBaseFileUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (upload.Size is < 1 or > 100 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(upload), "File size must be 1–104857600 bytes.");
        if (upload.Metadata.ValueKind != JsonValueKind.Object) throw new ArgumentException("File metadata must be a JSON object.", nameof(upload));
        var body = TerraBaseConnection.Json(upload, Json.TerraBaseFileUpload);
        return (await connection.SendAsync(HttpMethod.Post, FileBucketPath(bucketId) + "/files/upload", body, Json.TerraBaseFileUrl, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Verifies size and SHA-256 before publishing an upload. Requires its write rule; sent once.</summary>
    public async Task<TerraBaseStoredFile> CompleteFileUploadAsync(string bucketId, string fileId, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, FilePath(bucketId, fileId) + "/complete", null, Json.TerraBaseStoredFile, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Lists up to 50 completed files, filtered by read rules before pagination.</summary>
    public async Task<TerraBasePage<TerraBaseStoredFile>> ListBucketFilesAsync(string bucketId, string? after = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, FileBucketPath(bucketId) + "/files" + FileAfter(after), null,
            Json.TerraBasePageTerraBaseStoredFile, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Authorizes a 60-second attachment download. Existing capabilities remain valid until expiry. Sent once.</summary>
    public async Task<TerraBaseFileUrl> CreateFileDownloadAsync(string bucketId, string fileId, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, FilePath(bucketId, fileId) + "/download", null, Json.TerraBaseFileUrl, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Revokes new downloads and queues physical cleanup; storage is released after cleanup. Requires its write rule; sent once.</summary>
    public Task DeleteBucketFileAsync(string bucketId, string fileId, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, FilePath(bucketId, fileId), null, null, cancellationToken);

    private string FileBucketPath(string bucketId) => path + "/buckets/" + TerraScaleIds.Validate(bucketId);
    private string FilePath(string bucketId, string fileId) => FileBucketPath(bucketId) + "/files/" + TerraScaleIds.Validate(fileId);
    private static string FileAfter(string? after) => TerraBaseConnection.Query(("after", after is null ? null : TerraScaleIds.Validate(after)));
}
