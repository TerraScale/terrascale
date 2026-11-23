namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed class CreateFileUploadEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<CreateFileUploadRequest, Result<UploadCreated, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
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

    public override async ValueTask<Result<UploadCreated, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(CreateFileUploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Write, cancellationToken) switch
        {
            FileScope scope => await files.CreateUploadAsync(scope, request, cancellationToken) switch
            {
                UploadCreated upload => upload,
                Validation invalid => invalid,
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
