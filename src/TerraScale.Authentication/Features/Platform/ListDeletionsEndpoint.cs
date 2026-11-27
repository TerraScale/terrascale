using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Storage;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

public sealed class ListDeletionsEndpoint(AuthData data, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Get("/v1/{slug}/deletions");
        Name("deletions_list");
        Summary("List pending dashboard deletion proofs");
        Description("Private service API for the ts-dashboard confidential client using HTTP Basic. Returns at most 20 unacknowledged platform account, organization or project logical-deletion proofs with absent roots. Rotates delivery metadata for fair retries unless peek=1 is requested; does not certify physical erasure. Organization proofs use subjectId as organizationId; account proofs use null.");
        Tags("Members");
        Produces<DashboardDeletionList>();
        Produces<ErrorBody>(401);
        Produces<ErrorBody>(404);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        DashboardClientAuthentication.Refuse(HttpContext, options)
        ?? TypedResults.Json(
            await DashboardDeletionProofs.ListAsync(data, IsPeek(HttpContext.Request.Query["peek"].ToString()), cancellationToken),
            DashboardDeletionJson.Default.DashboardDeletionList);

    private static bool IsPeek(string value) =>
        string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
