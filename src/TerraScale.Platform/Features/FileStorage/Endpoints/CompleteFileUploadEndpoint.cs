namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public readonly union CompleteFileUploadReply(
    FileObject,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public sealed class CompleteFileUploadEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<BucketFileRequest, CompleteFileUploadReply>
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

    public override async ValueTask<CompleteFileUploadReply> HandleAsync(BucketFileRequest request, CancellationToken cancellationToken)
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

        async ValueTask<CompleteFileUploadReply> HandleAuthorizedAsync(FileScope scope)
        {
            var operation = await files.CompleteUploadAsync(scope, request.Bucket, request.File, cancellationToken);
            return operation switch
            {
                FileObject file => file,
                PlatformForbidden forbidden => forbidden,
                PlatformNotFound missing => missing,
                PlatformConflict conflict => conflict,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
