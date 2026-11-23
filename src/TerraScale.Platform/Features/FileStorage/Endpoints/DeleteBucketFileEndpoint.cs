namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed class DeleteBucketFileEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<BucketFileRequest, Result<Done, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
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

    public override async ValueTask<Result<Done, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(BucketFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Write, cancellationToken) switch
        {
            FileScope scope => await files.DeleteFileAsync(scope, request.Bucket, request.File, cancellationToken) switch
            {
                Done done => done,
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
