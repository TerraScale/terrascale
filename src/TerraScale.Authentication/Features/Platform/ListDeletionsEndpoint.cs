using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Private dashboard handoff of verified platform logical deletions.</summary>
public sealed class ListDeletionsEndpoint(AuthStore store, TerraBaseDatabase database, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/{slug}/deletions");
        Name("deletions_list");
        Summary("List pending dashboard deletion proofs");
        Description("Private service API for the ts-dashboard confidential client using HTTP Basic. Returns at most 20 unacknowledged platform account, organization or project logical-deletion proofs with absent roots. Rotates delivery metadata for fair retries; does not certify physical erasure. Organization proofs use subjectId as organizationId; account proofs use null.");
        Tags("Members");
        Produces<DashboardDeletionList>();
        Produces<ErrorBody>(401);
        Produces<ErrorBody>(404);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        DashboardClientAuthentication.Refuse(this.HttpContext, options)
        ?? TypedResults.Json(await DashboardDeletionProofs.ListAsync(store, database, cancellationToken), DashboardDeletionJson.Default.DashboardDeletionList);
}
