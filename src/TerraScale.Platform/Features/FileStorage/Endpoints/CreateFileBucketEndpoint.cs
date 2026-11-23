namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed class CreateFileBucketEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<CreateFileBucketRequest, Result<BucketCreated, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
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

    public override async ValueTask<Result<BucketCreated, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(CreateFileBucketRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Write, cancellationToken) switch
        {
            FileScope scope => await files.CreateBucketAsync(scope, request.Name, request.Rules, cancellationToken) switch
            {
                FileBucket bucket => new BucketCreated(bucket),
                Validation invalid => invalid,
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
