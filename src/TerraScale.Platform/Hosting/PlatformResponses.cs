using TerraScale.Platform.Features.FileStorage;
using TerraScale.Platform.Features.Functions;
using TerraScale.Platform.Features.Shapes;

namespace TerraScale.Platform.Hosting;

public static class PlatformResponses
{
    public static void Configure(EndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Title = "TerraScale Platform (ts-platform)";
        options.Version = "v1";
        options
            .MapResponse<BucketDeleted>(static _ => TypedResults.NoContent(), StatusCodes.Status204NoContent)
            .MapResponse<FileDeleted>(static _ => TypedResults.NoContent(), StatusCodes.Status204NoContent)
            .MapResponse<FunctionDeleted>(static _ => TypedResults.NoContent(), StatusCodes.Status204NoContent)
            .MapResponse<SecretDeleted>(static _ => TypedResults.NoContent(), StatusCodes.Status204NoContent)
            .MapResponse<ProjectClosed>(static _ => TypedResults.NoContent(), StatusCodes.Status204NoContent)
            .MapResponse<PlatformInvalidRequest>(static error => ErrorResults.From(error), StatusCodes.Status400BadRequest, typeof(ApiErrorEnvelope))
            .MapResponse<PlatformUnauthorized>(static error => ErrorResults.From(error), StatusCodes.Status401Unauthorized, typeof(ApiErrorEnvelope))
            .MapResponse<PlatformForbidden>(static error => ErrorResults.From(error), StatusCodes.Status403Forbidden, typeof(ApiErrorEnvelope))
            .MapResponse<PlatformNotFound>(static error => ErrorResults.From(error), StatusCodes.Status404NotFound, typeof(ApiErrorEnvelope))
            .MapResponse<PlatformConflict>(static error => ErrorResults.From(error), StatusCodes.Status409Conflict, typeof(ApiErrorEnvelope))
            .MapResponse<PlatformPayloadTooLarge>(static error => ErrorResults.From(error), StatusCodes.Status413PayloadTooLarge, typeof(ApiErrorEnvelope))
            .MapResponse<PlatformQuotaExceeded>(static error => ErrorResults.From(error), StatusCodes.Status429TooManyRequests, typeof(ApiErrorEnvelope))
            .MapResponse<PlatformUnavailable>(static error => ErrorResults.From(error), StatusCodes.Status503ServiceUnavailable, typeof(ApiErrorEnvelope))
            .MapResponse<FunctionList>(
                static list => TypedResults.Json(list.Items, FunctionsJsonContext.Default.IReadOnlyListFunctionBody),
                StatusCodes.Status200OK,
                typeof(IReadOnlyList<FunctionBody>))
            .MapResponse<FunctionLogList>(
                static list => TypedResults.Json(list.Items, FunctionsJsonContext.Default.IReadOnlyListFunctionLogBody),
                StatusCodes.Status200OK,
                typeof(IReadOnlyList<FunctionLogBody>))
            .MapResponse<FunctionSecretList>(
                static list => TypedResults.Json(list.Items, FunctionsJsonContext.Default.IReadOnlyListFunctionSecretBody),
                StatusCodes.Status200OK,
                typeof(IReadOnlyList<FunctionSecretBody>))
            .MapResponse<BucketCreated>(
                static created => TypedResults.Json(created.Bucket, FileStorageJsonContext.Default.FileBucket, statusCode: StatusCodes.Status201Created),
                StatusCodes.Status201Created,
                typeof(FileBucket))
            .MapResponse<UploadCreated>(
                static created => TypedResults.Json(created.Upload, FileStorageJsonContext.Default.FileSignedUrl, statusCode: StatusCodes.Status201Created),
                StatusCodes.Status201Created,
                typeof(FileSignedUrl))
            .MapResponse<ShapeMirrorManifest>(
                static manifest => TypedResults.Json(manifest, ShapesJsonContext.Default.ShapeMirrorManifest),
                StatusCodes.Status200OK,
                typeof(ShapeMirrorManifest))
            .MapResponse<ShapeKeyResponse>(
                static key => TypedResults.Json(key, ShapesJsonContext.Default.ShapeKeyResponse),
                StatusCodes.Status200OK,
                typeof(ShapeKeyResponse))
            .MapResponse<RowResponse>(
                static row => TypedResults.Json(row, ShapesJsonContext.Default.RowResponse),
                StatusCodes.Status200OK,
                typeof(RowResponse));
    }
}
