using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.FileStorage;

/// <summary>CreateFileDownload through the authorized project file API.</summary>
public sealed class CreateFileDownloadEndpoint(IFileStorage files) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(FileStorageRoute.File + "/download");
        Name("createFileDownload");
        Summary("CreateFileDownload");
        Description(FileStorageRoute.Description);
        Tags("File storage");
        Produces<FileSignedUrl>(200);
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
        FileStorageRoute.RunAsync(HttpContext, () => files.DownloadAsync(FileStorageRoute.BucketId(HttpContext), FileStorageRoute.FileId(HttpContext), cancellationToken), 200);
}
