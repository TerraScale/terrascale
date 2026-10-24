using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.FileStorage;

/// <summary>DeleteFileBucket through the authorized project file API.</summary>
public sealed class DeleteFileBucketEndpoint(IFileStorage files) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(FileStorageRoute.Bucket);
        Name("deleteFileBucket");
        Summary("DeleteFileBucket");
        Description(FileStorageRoute.Description);
        Tags("File storage");
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(400);
        Produces<ErrorBody>(403);
        Produces<ErrorBody>(404);
        Produces<ErrorBody>(409);
        Produces<ErrorBody>(429);
        Produces<ErrorBody>(503);
        RequireAuthorization("documents:write");
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        FileStorageRoute.RunAsync(HttpContext, () => files.DeleteBucketAsync(FileStorageRoute.BucketId(HttpContext), cancellationToken), 204);
}
