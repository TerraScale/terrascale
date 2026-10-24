using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.FileStorage;

/// <summary>ListBucketFiles through the authorized project file API.</summary>
public sealed class ListBucketFilesEndpoint(IFileStorage files) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(FileStorageRoute.Files);
        Name("listBucketFiles");
        Summary("ListBucketFiles");
        Description(FileStorageRoute.Description);
        Tags("File storage");
        Produces<FileObjectPage>(200);
        Produces<ErrorBody>(400);
        Produces<ErrorBody>(403);
        Produces<ErrorBody>(404);
        Produces<ErrorBody>(409);
        Produces<ErrorBody>(429);
        Produces<ErrorBody>(503);
        RequireAuthorization("documents:read");
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        FileStorageRoute.RunAsync(HttpContext, () => files.ListFilesAsync(FileStorageRoute.BucketId(HttpContext), FileStorageRoute.After(HttpContext), cancellationToken), 200);
}
