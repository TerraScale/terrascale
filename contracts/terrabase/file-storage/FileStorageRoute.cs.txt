using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;

namespace TerraBase.Features.FileStorage;

internal static class FileStorageRoute
{
    public const string Buckets = ProjectApiRoute.Project + "/buckets";
    public const string Bucket = Buckets + "/{bucket}";
    public const string Files = Bucket + "/files";
    public const string File = Files + "/{file}";
    public const string Description = "Project file buckets. Bearer ts-auth project or end-user token; documents:read/write scopes. "
        + "Bucket creation, listing, rules and deletion require project credentials; database-restricted credentials cannot access files. "
        + "Rules use the document owner JSON Pointer, read/write owner/public/roles policies, evaluated against immutable file metadata. "
        + "Missing grants deny access. File IDs are server-generated ULIDs. Upload: name, contentType, size (1–100 MiB), base64 sha256, metadata; "
        + "reserve shared database/file storage, PUT the exact bytes with every returned header to the five-minute URL, then POST complete. "
        + "Files are immutable; checksum and size are verified before reads. Download capabilities expire after 60 seconds; existing capabilities remain valid until expiry. "
        + "Keep URLs private. List accepts after (exclusive ULID), 50 results per page, with authorized filtering before pagination. "
        + "Delete queues cleanup; reserved bytes remain billed until physical deletion succeeds. Replicas are never counted again. No automatic mutation retries.";

    public static string BucketId(HttpContext context) => (string)context.Request.RouteValues["bucket"]!;
    public static string FileId(HttpContext context) => (string)context.Request.RouteValues["file"]!;
    public static string? After(HttpContext context) => context.Request.Query.TryGetValue("after", out var value) ? value.ToString() : null;

    public static async ValueTask<IResult> RunAsync(HttpContext context, Func<ValueTask<FileStorageResult>> operation, int success)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (ProjectAccess.Current is not { DatabaseName: null })
        {
            return ErrorResponse.From(new Forbidden("an unrestricted project token is required"));
        }
        if (context.Request.Query.Keys.Any(key => key != "after") || After(context) is { } after && !EntityIds.IsValid(after))
        {
            return ErrorResponse.From(new Validation("only after (a canonical ULID) is accepted"));
        }
        return await operation().ConfigureAwait(false) switch
        {
            FileBucket bucket => TypedResults.Json(bucket, statusCode: success),
            FileBucketPage page => TypedResults.Ok(page),
            FileObject file => TypedResults.Ok(file),
            FileObjectPage page => TypedResults.Ok(page),
            FileSignedUrl signed => TypedResults.Json(signed, statusCode: success),
            Done => TypedResults.NoContent(),
            Validation error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            QuotaExceeded error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}

/// <summary>Registers the always-on file API, logical usage and bounded upload cleanup.</summary>
public static class FileStorageExtensions
{
    /// <summary>Adds project file buckets without feature flags or customer configuration.</summary>
    public static IServiceCollection AddTerraBaseFileStorage(this IServiceCollection services) => services
        .AddSingleton<IFileObjectStore, S3FileObjectStore>()
        .AddSingleton<IFileStorageUsage, FileStorageUsage>()
        .AddSingleton<FileStorageService>()
        .AddSingleton<IFileStorage>(provider => provider.GetRequiredService<FileStorageService>())
        .AddHostedService(provider => provider.GetRequiredService<FileStorageService>());
}
