namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public readonly union ListBucketFilesReply(
    FileObjectPage,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class ListBucketFilesEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<ListBucketFilesRequest, ListBucketFilesReply>
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

    public override async ValueTask<ListBucketFilesReply> HandleAsync(ListBucketFilesRequest request, CancellationToken cancellationToken)
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

        async ValueTask<ListBucketFilesReply> HandleAuthorizedAsync(FileScope scope)
        {
            var operation = await files.ListFilesAsync(scope, request.Bucket, request.After, cancellationToken);
            return operation switch
            {
                FileObjectPage page => page,
                PlatformForbidden forbidden => forbidden,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
