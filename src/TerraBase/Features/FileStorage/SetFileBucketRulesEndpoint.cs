using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.FileStorage;

/// <summary>SetFileBucketRules through the authorized project file API.</summary>
public sealed class SetFileBucketRulesEndpoint(IFileStorage files) : Endpoint<FileBucketRulesRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(FileStorageRoute.Bucket + "/rules");
        Name("setFileBucketRules");
        Summary("SetFileBucketRules");
        Description(FileStorageRoute.Description);
        Tags("File storage");
        Metadata(new RequestBodyMetadata(typeof(FileBucketRulesRequest), "application/json"));
        Produces<FileBucket>(200);
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
    public override ValueTask<IResult> HandleAsync(FileBucketRulesRequest request, CancellationToken cancellationToken) =>
        FileStorageRoute.RunAsync(HttpContext, () => files.SetRulesAsync(FileStorageRoute.BucketId(HttpContext), request, cancellationToken), 200);
}
