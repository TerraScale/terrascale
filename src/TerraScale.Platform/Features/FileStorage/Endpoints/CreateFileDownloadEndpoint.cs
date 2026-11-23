namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed class CreateFileDownloadEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<BucketFileRequest, Result<FileSignedUrl, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
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

    public override async ValueTask<Result<FileSignedUrl, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(BucketFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Read, cancellationToken) switch
        {
            FileScope scope => await files.DownloadAsync(scope, request.Bucket, request.File, cancellationToken) switch
            {
                FileSignedUrl signed => signed,
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
