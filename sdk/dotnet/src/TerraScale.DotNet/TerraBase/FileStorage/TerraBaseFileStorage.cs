using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseFilePolicy(bool Owner, bool Public, IReadOnlyList<string> Roles);
public sealed record TerraBaseFileRules(string? Owner, TerraBaseFilePolicy Read, TerraBaseFilePolicy Write)
{
    [JsonConstructor]
    internal TerraBaseFileRules(TerraBaseFilePolicy Read, TerraBaseFilePolicy Write, string? Owner = null)
        : this(Owner, Read, Write)
    {
    }
}
public sealed record TerraBaseFileBucket(string Id, string Name, string Region, TerraBaseFileRules Rules);
public sealed record TerraBaseStoredFile(string Id, string Name, string ContentType, long Size, string Sha256,
    JsonElement Metadata, string Status, DateTimeOffset UploadExpiresAt);
public sealed record TerraBaseFileUrl(string Id, string Url, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers)
{
    public override string ToString() => $"File capability for {Id} (URL redacted)";
}
public sealed record TerraBaseFileUpload(string Name, string ContentType, long Size, string Sha256, JsonElement Metadata);
internal sealed record TerraBaseFileBucketRequest(string Name, TerraBaseFileRules Rules);
internal sealed record TerraBaseFileRulesRequest(TerraBaseFileRules Rules);

public sealed partial class TerraBaseProjectClient
{
    public async Task<TerraBaseFileBucket> CreateFileBucketAsync(string name, TerraBaseFileRules rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var body = TerraBaseConnection.Json(new TerraBaseFileBucketRequest(name, rules), Json.TerraBaseFileBucketRequest);
        return (await connection.SendAsync(HttpMethod.Post, path + "/buckets", body, Json.TerraBaseFileBucket, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public async Task<TerraBasePage<TerraBaseFileBucket>> ListFileBucketsAsync(string? after = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/buckets" + FileAfter(after), null,
            Json.TerraBasePageTerraBaseFileBucket, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseFileBucket> SetFileBucketRulesAsync(string bucketId, TerraBaseFileRules rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var body = TerraBaseConnection.Json(new TerraBaseFileRulesRequest(rules), Json.TerraBaseFileRulesRequest);
        return (await connection.SendAsync(HttpMethod.Put, FileBucketPath(bucketId) + "/rules", body, Json.TerraBaseFileBucket, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public Task DeleteFileBucketAsync(string bucketId, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, FileBucketPath(bucketId), null, null, cancellationToken);

    public async Task<TerraBaseFileUrl> CreateFileUploadAsync(string bucketId, TerraBaseFileUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (upload.Size is < 1 or > 100 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(upload), "File size must be 1–104857600 bytes.");
        if (upload.Metadata.ValueKind != JsonValueKind.Object) throw new ArgumentException("File metadata must be a JSON object.", nameof(upload));
        var body = TerraBaseConnection.Json(upload, Json.TerraBaseFileUpload);
        return (await connection.SendAsync(HttpMethod.Post, FileBucketPath(bucketId) + "/files/upload", body, Json.TerraBaseFileUrl, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public async Task<TerraBaseStoredFile> CompleteFileUploadAsync(string bucketId, string fileId, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, FilePath(bucketId, fileId) + "/complete", null, Json.TerraBaseStoredFile, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBasePage<TerraBaseStoredFile>> ListBucketFilesAsync(string bucketId, string? after = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, FileBucketPath(bucketId) + "/files" + FileAfter(after), null,
            Json.TerraBasePageTerraBaseStoredFile, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseFileUrl> CreateFileDownloadAsync(string bucketId, string fileId, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, FilePath(bucketId, fileId) + "/download", null, Json.TerraBaseFileUrl, null, cancellationToken).ConfigureAwait(false)).Value;

    public Task DeleteBucketFileAsync(string bucketId, string fileId, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, FilePath(bucketId, fileId), null, null, cancellationToken);

    private string FileBucketPath(string bucketId) => path + "/buckets/" + TerraScaleIds.Validate(bucketId);
    private string FilePath(string bucketId, string fileId) => FileBucketPath(bucketId) + "/files/" + TerraScaleIds.Validate(fileId);
    private static string FileAfter(string? after) => TerraBaseConnection.Query(("after", after is null ? null : TerraScaleIds.Validate(after)));
}
