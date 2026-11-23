namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed class DeleteFileBucketEndpoint(PlatformAccess access, FileStorageService files)
    : Endpoint<FileBucketRequest, Result<Done, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Delete(FileRoutes.Bucket);
        Name("deleteFileBucket");
        Summary("Deletes an empty file bucket");
        Description(FileRoutes.Description);
        Tags("File storage");
        AllowAnonymous();
    }

    public override async ValueTask<Result<Done, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(FileBucketRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FileScopes.ResolveAsync(access, HttpContext, request.Project, Scopes.Write, cancellationToken) switch
        {
            FileScope scope => await files.DeleteBucketAsync(scope, request.Bucket, cancellationToken) switch
            {
                Done done => done,
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
