namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public readonly union CreateFileBucketReply(
    BucketCreated,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public sealed class CreateFileBucketEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<CreateFileBucketRequest, CreateFileBucketReply>
{
    public override void Configure()
    {
        Post(FileRoutes.Buckets);
        Name("createFileBucket");
        Summary("Creates a file bucket");
        Description(FileRoutes.Description);
        Tags("File storage");
        AllowAnonymous();
    }

    public override async ValueTask<CreateFileBucketReply> HandleAsync(CreateFileBucketRequest request, CancellationToken cancellationToken)
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

        async ValueTask<CreateFileBucketReply> HandleAuthorizedAsync(FileScope scope)
        {
            var operation = await files.CreateBucketAsync(scope, request.Name, request.Rules, cancellationToken);
            return operation switch
            {
                FileBucket bucket => new BucketCreated(bucket),
                PlatformInvalidRequest invalid => invalid,
                PlatformForbidden forbidden => forbidden,
                PlatformNotFound missing => missing,
                PlatformConflict conflict => conflict,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
