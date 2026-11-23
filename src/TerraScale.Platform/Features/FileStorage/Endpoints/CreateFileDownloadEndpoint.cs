namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public readonly union CreateFileDownloadReply(
    FileSignedUrl,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public sealed class CreateFileDownloadEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<BucketFileRequest, CreateFileDownloadReply>
{
    public override void Configure()
    {
        Post(FileRoutes.File + "/download");
        Name("createFileDownload");
        Summary("Presigns a 60-second file download");
        Description(FileRoutes.Description);
        Tags("File storage");
        AllowAnonymous();
    }

    public override async ValueTask<CreateFileDownloadReply> HandleAsync(BucketFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Read, cancellationToken);
        return outcome switch
        {
            FileScope scope => await HandleAuthorizedAsync(scope),
            PlatformInvalidRequest invalid => invalid,
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<CreateFileDownloadReply> HandleAuthorizedAsync(FileScope scope)
        {
            var operation = await files.DownloadAsync(scope, request.Bucket, request.File, cancellationToken);
            return operation switch
            {
                FileSignedUrl signed => signed,
                PlatformForbidden forbidden => forbidden,
                PlatformNotFound missing => missing,
                PlatformConflict conflict => conflict,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
