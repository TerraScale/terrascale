namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public readonly union ListFileBucketsReply(
    FileBucketPage,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class ListFileBucketsEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<ListFileBucketsRequest, ListFileBucketsReply>
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

    public override async ValueTask<ListFileBucketsReply> HandleAsync(ListFileBucketsRequest request, CancellationToken cancellationToken)
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

        async ValueTask<ListFileBucketsReply> HandleAuthorizedAsync(FileScope scope)
        {
            var operation = await files.ListBucketsAsync(scope, request.After, cancellationToken);
            return operation switch
            {
                FileBucketPage page => page,
                PlatformForbidden forbidden => forbidden,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
