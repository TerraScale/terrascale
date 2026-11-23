namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed class CompleteFileUploadEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<BucketFileRequest, Result<FileObject, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Post(FileRoutes.File + "/complete");
        Name("completeFileUpload");
        Summary("Verifies an uploaded file and makes it readable");
        Description(FileRoutes.Description);
        Tags("File storage");
        AllowAnonymous();
    }

    public override async ValueTask<Result<FileObject, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(BucketFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Write, cancellationToken) switch
        {
            FileScope scope => await files.CompleteUploadAsync(scope, request.Bucket, request.File, cancellationToken) switch
            {
                FileObject file => file,
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Conflict conflict => conflict,
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
