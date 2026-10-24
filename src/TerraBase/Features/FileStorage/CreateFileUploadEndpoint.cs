using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.FileStorage;

/// <summary>CreateFileUpload through the authorized project file API.</summary>
public sealed class CreateFileUploadEndpoint(IFileStorage files) : Endpoint<CreateFileUploadRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(FileStorageRoute.Files + "/upload");
        Name("createFileUpload");
        Summary("CreateFileUpload");
        Description(FileStorageRoute.Description);
        Tags("File storage");
        Metadata(new RequestBodyMetadata(typeof(CreateFileUploadRequest), "application/json"));
        Produces<FileSignedUrl>(201);
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
    public override ValueTask<IResult> HandleAsync(CreateFileUploadRequest request, CancellationToken cancellationToken) =>
        FileStorageRoute.RunAsync(HttpContext, () => files.CreateUploadAsync(FileStorageRoute.BucketId(HttpContext), request, cancellationToken), 201);
}
