namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public readonly union SetFileBucketRulesReply(
    FileBucket,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public sealed class SetFileBucketRulesEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<FileBucketRulesRequest, SetFileBucketRulesReply>
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

    public override async ValueTask<SetFileBucketRulesReply> HandleAsync(FileBucketRulesRequest request, CancellationToken cancellationToken)
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

        async ValueTask<SetFileBucketRulesReply> HandleAuthorizedAsync(FileScope scope)
        {
            var operation = await files.SetRulesAsync(scope, request.Bucket, request.Rules, cancellationToken);
            return operation switch
            {
                FileBucket bucket => bucket,
                PlatformInvalidRequest invalid => invalid,
                PlatformForbidden forbidden => forbidden,
                PlatformNotFound missing => missing,
                PlatformConflict conflict => conflict,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
