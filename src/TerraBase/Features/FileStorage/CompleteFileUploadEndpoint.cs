using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.FileStorage;

/// <summary>CompleteFileUpload through the authorized project file API.</summary>
public sealed class CompleteFileUploadEndpoint(IFileStorage files) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(FileStorageRoute.File + "/complete");
        Name("completeFileUpload");
        Summary("CompleteFileUpload");
        Description(FileStorageRoute.Description);
        Tags("File storage");
        Produces<FileObject>(200);
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
        FileStorageRoute.RunAsync(HttpContext, () => files.CompleteUploadAsync(FileStorageRoute.BucketId(HttpContext), FileStorageRoute.FileId(HttpContext), cancellationToken), 200);
}
