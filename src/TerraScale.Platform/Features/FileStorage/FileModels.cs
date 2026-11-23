using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TerraScale.Platform.Features.FileStorage;

public sealed record FileAccessPolicy(bool Owner, bool Public, IReadOnlyList<string>? Roles);

public sealed record FileBucket(string Id, string Name, string Region, FileAccessRules Rules);

public sealed record FileObject(
    string Id,
    string Name,
    string ContentType,
    long Size,
    string Sha256,
    JsonElement Metadata,
    string Status,
    DateTimeOffset UploadExpiresAt,
    DateTimeOffset? DeleteAfter = null);

public sealed record FileSignedUrl(string Id, string Url, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers);

public sealed record FileBucketPage(IReadOnlyList<FileBucket> Items, string? Next);

public sealed record FileObjectPage(IReadOnlyList<FileObject> Items, string? Next);

public sealed record BucketCreated(FileBucket Bucket);

public sealed record UploadCreated(FileSignedUrl Upload);

public sealed record CreateFileBucketRequest(string Project, string? Name, FileAccessRules? Rules);

public sealed record ListFileBucketsRequest(string Project, string? After);

public sealed record FileBucketRulesRequest(string Project, string Bucket, FileAccessRules? Rules);

public sealed record FileBucketRequest(string Project, string Bucket);

public sealed record CreateFileUploadRequest(string Project, string Bucket, string? Name, string? ContentType, long Size, string? Sha256, JsonElement Metadata);

public sealed record ListBucketFilesRequest(string Project, string Bucket, string? After);

public sealed record BucketFileRequest(string Project, string Bucket, string File);

public sealed record FileScope(Ulid Organization, Ulid Project, AccessContext Access);

public static partial class FileRoutes
{
    public const string Buckets = "/v1/projects/{project}/buckets";
    public const string Bucket = Buckets + "/{bucket}";
    public const string Files = Bucket + "/files";
    public const string File = Files + "/{file}";
    public const string Description = "Project file buckets. Bearer project or end-user token, API key or peer credential; sql:read/write scopes. "
        + "Bucket creation, listing, rules and deletion require project credentials; database-restricted credentials cannot access files. "
        + "Rules use the owner JSON Pointer, read/write owner/public/roles policies, evaluated against immutable file metadata. "
        + "Upload: name, contentType, size (1-100 MiB), base64 sha256, metadata; PUT the exact bytes with every returned header to the five-minute URL, "
        + "then POST complete. Download URLs expire after 60 seconds. List accepts after (exclusive ULID), 50 results per page.";

    public static bool IsName([NotNullWhen(true)] string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FileBucket))]
[JsonSerializable(typeof(FileObject))]
[JsonSerializable(typeof(FileSignedUrl))]
[JsonSerializable(typeof(FileBucketPage))]
[JsonSerializable(typeof(FileObjectPage))]
[JsonSerializable(typeof(FileAccessRules))]
[JsonSerializable(typeof(CreateFileBucketRequest))]
[JsonSerializable(typeof(ListFileBucketsRequest))]
[JsonSerializable(typeof(FileBucketRulesRequest))]
[JsonSerializable(typeof(FileBucketRequest))]
[JsonSerializable(typeof(CreateFileUploadRequest))]
[JsonSerializable(typeof(ListBucketFilesRequest))]
[JsonSerializable(typeof(BucketFileRequest))]
public sealed partial class FileStorageJsonContext : JsonSerializerContext;
