namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed class SetFileBucketRulesEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<FileBucketRulesRequest, Result<FileBucket, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Put(FileRoutes.Bucket + "/rules");
        Name("setFileBucketRules");
        Summary("Replaces a bucket's access rules");
        Description(FileRoutes.Description);
        Tags("File storage");
        AllowAnonymous();
    }

    public override async ValueTask<Result<FileBucket, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(FileBucketRulesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Write, cancellationToken) switch
        {
            FileScope scope => await files.SetRulesAsync(scope, request.Bucket, request.Rules, cancellationToken) switch
            {
                FileBucket bucket => bucket,
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
