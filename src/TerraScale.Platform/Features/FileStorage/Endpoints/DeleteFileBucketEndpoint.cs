namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public readonly union DeleteFileBucketReply(
    BucketDeleted,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public sealed class DeleteFileBucketEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<FileBucketRequest, DeleteFileBucketReply>
{
    public override void Configure()
    {
        Delete(FileRoutes.Bucket);
        Name("deleteFileBucket");
        Summary("Deletes an empty file bucket");
        Description(FileRoutes.Description);
        Tags("File storage");
        AllowAnonymous();
    }

    public override async ValueTask<DeleteFileBucketReply> HandleAsync(FileBucketRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Write, cancellationToken);
        return outcome switch
        {
            FileScope scope => await HandleAuthorizedAsync(scope),
            PlatformInvalidRequest invalid => invalid,
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<DeleteFileBucketReply> HandleAuthorizedAsync(FileScope scope)
        {
            var operation = await files.DeleteBucketAsync(scope, request.Bucket, cancellationToken);
            return operation switch
            {
                BucketDeleted deleted => deleted,
                PlatformForbidden forbidden => forbidden,
                PlatformNotFound missing => missing,
                PlatformConflict conflict => conflict,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
