using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.FileStorage;

/// <summary>CreateFileBucket through the authorized project file API.</summary>
public sealed class CreateFileBucketEndpoint(IFileStorage files) : Endpoint<CreateFileBucketRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(FileStorageRoute.Buckets);
        Name("createFileBucket");
        Summary("CreateFileBucket");
        Description(FileStorageRoute.Description);
        Tags("File storage");
        Metadata(new RequestBodyMetadata(typeof(CreateFileBucketRequest), "application/json"));
        Produces<FileBucket>(201);
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
    public override ValueTask<IResult> HandleAsync(CreateFileBucketRequest request, CancellationToken cancellationToken) =>
        FileStorageRoute.RunAsync(HttpContext, () => files.CreateBucketAsync(request, cancellationToken), 201);
}
