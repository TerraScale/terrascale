using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Store;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Durable acknowledgement after dashboard verified its own scoped cleanup.</summary>
public sealed class AcknowledgeDeletionEndpoint(AuthStore store, AuthOptions options, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/{slug}/deletions/{id}/acknowledge");
        Name("deletion_acknowledge");
        Summary("Acknowledge dashboard deletion cleanup");
        Description("Private ts-dashboard HTTP Basic service API. After its own cleanup is verified, acknowledge the exact proof id, scope, subjectId and organizationId. Requires an existing verified platform deletion job and an absent auth root. Idempotent while that same proof remains retained; unknown, mismatched or retired proofs return 404. Allows history erasure and eventual fence retirement.");
        Tags("Members");
        Metadata(new AcceptsMetadata(["application/json"], typeof(DashboardDeletionAcknowledgement)));
        Produces(204);
        foreach (var status in new[] { 400, 401, 404, 409, 413, 415 })
        {
            Produces<ErrorBody>(status);
        }

        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (DashboardClientAuthentication.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }

        if (!this.HttpContext.Request.HasJsonContentType())
        {
            throw new ApiException(415, "unsupported_media_type", "Send the body as application/json.");
        }

        if (this.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = 4096;
        }

        DashboardDeletionAcknowledgement request;
        try
        {
            request = await this.HttpContext.Request.ReadFromJsonAsync(DashboardDeletionJson.Default.DashboardDeletionAcknowledgement, cancellationToken)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ApiException(400, "invalid_request", "Send an exact deletion acknowledgement JSON object.");
        }

        await DashboardDeletionProofs.AcknowledgeAsync(store, clock, this.HttpContext.GetRouteValue("id") as string, request, cancellationToken);
        return TypedResults.NoContent();
    }
}
