namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public readonly union DeleteBucketFileReply(
    FileDeleted,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class DeleteBucketFileEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<BucketFileRequest, DeleteBucketFileReply>
{
    public override void Configure()
    {
        Delete(FileRoutes.File);
        Name("deleteBucketFile");
        Summary("Deletes a file; its object is erased after outstanding links expire");
        Description(FileRoutes.Description);
        Tags("File storage");
        AllowAnonymous();
    }

    public override async ValueTask<DeleteBucketFileReply> HandleAsync(BucketFileRequest request, CancellationToken cancellationToken)
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

        async ValueTask<DeleteBucketFileReply> HandleAuthorizedAsync(FileScope scope)
        {
            var operation = await files.DeleteFileAsync(scope, request.Bucket, request.File, cancellationToken);
            return operation switch
            {
                FileDeleted deleted => deleted,
                PlatformForbidden forbidden => forbidden,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
