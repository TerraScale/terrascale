using TerraScale.Endpoints;
using TerraScale.TerraBase.Errors;
using TerraScale.TerraBase.Features.Documents;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>GET /v1/management/commits/{id}</c>: the recorded response of a committed commit.</summary>
public sealed class GetCommitEndpoint(IManagementCommits commits) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Commits + "/{id}");
        Name("getCommit");
        Summary("Reads the outcome of a commit");
        Description($"The response the acting user's commit answered, recorded in the commit's own transaction. 404 when it has not committed (yet: the request may still be running), failed, is older than 7 days or belongs to another user. A 404 is not final: resend the same id and byte-identical body, never a new id. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.Tag);
        Produces<CommitBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await commits.GetCommitAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.Value(HttpContext, "id"), cancellationToken) switch
        {
            byte[] body => TypedResults.Bytes(body, DocumentRoute.JsonMediaType),
            NotFound error => ErrorResponse.From(error),
        };
}
