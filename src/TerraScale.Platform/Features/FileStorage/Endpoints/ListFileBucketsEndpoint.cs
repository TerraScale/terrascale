namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed class ListFileBucketsEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<ListFileBucketsRequest, Result<FileBucketPage, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(FileRoutes.Buckets);
        Name("listFileBuckets");
        Summary("Lists file buckets");
        Description(FileRoutes.Description);
        Tags("File storage");
        AllowAnonymous();
    }

    public override async ValueTask<Result<FileBucketPage, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(ListFileBucketsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Read, cancellationToken) switch
        {
            FileScope scope => await files.ListBucketsAsync(scope, request.After, cancellationToken) switch
            {
                FileBucketPage page => page,
                Forbidden forbidden => forbidden,
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
