namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed class ListBucketFilesEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<ListBucketFilesRequest, Result<FileObjectPage, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(FileRoutes.Files);
        Name("listBucketFiles");
        Summary("Lists readable files of a bucket");
        Description(FileRoutes.Description);
        Tags("File storage");
        AllowAnonymous();
    }

    public override async ValueTask<Result<FileObjectPage, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(ListBucketFilesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Read, cancellationToken) switch
        {
            FileScope scope => await files.ListFilesAsync(scope, request.Bucket, request.After, cancellationToken) switch
            {
                FileObjectPage page => page,
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
