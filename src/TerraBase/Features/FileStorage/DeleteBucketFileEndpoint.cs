using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.FileStorage;

/// <summary>DeleteBucketFile through the authorized project file API.</summary>
public sealed class DeleteBucketFileEndpoint(IFileStorage files) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(FileStorageRoute.File);
        Name("deleteBucketFile");
        Summary("DeleteBucketFile");
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
        FileStorageRoute.RunAsync(HttpContext, () => files.DeleteFileAsync(FileStorageRoute.BucketId(HttpContext), FileStorageRoute.FileId(HttpContext), cancellationToken), 204);
}
