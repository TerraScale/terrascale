namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public readonly union CreateFileUploadReply(
    UploadCreated,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class CreateFileUploadEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<CreateFileUploadRequest, CreateFileUploadReply>
{
    public override void Configure()
    {
        Post(FileRoutes.Files + "/upload");
        Name("createFileUpload");
        Summary("Reserves a file and presigns its upload");
        Description(FileRoutes.Description);
        Tags("File storage");
        AllowAnonymous();
    }

    public override async ValueTask<CreateFileUploadReply> HandleAsync(CreateFileUploadRequest request, CancellationToken cancellationToken)
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

        async ValueTask<CreateFileUploadReply> HandleAuthorizedAsync(FileScope scope)
        {
            var operation = await files.CreateUploadAsync(scope, request, cancellationToken);
            return operation switch
            {
                UploadCreated upload => upload,
                PlatformInvalidRequest invalid => invalid,
                PlatformForbidden forbidden => forbidden,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
