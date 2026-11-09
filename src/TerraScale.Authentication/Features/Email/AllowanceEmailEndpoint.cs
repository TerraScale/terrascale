using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Hosting;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Email;

public sealed record AllowanceEmailRequest(string OrganizationId, string BillingMonth, string Metric, int Threshold, long Used, long Allowance);

public sealed record AllowanceEmailResponse(string Status);

public sealed class AllowanceEmailEndpoint(PlatformDirectory directory, OwnerDeliveries deliveries, AuthOptions options, TimeProvider clock) : EndpointWithoutRequest<IResult>
{

    public override void Configure()
    {
        Post("/v1/{slug}/email/allowance");
        Name("email_allowance_send");
        Summary("Notify the current organization owners of an allowance threshold");
        Description("Dashboard ClientBasic email authority. ts-auth resolves current owners internally; no recipient identities are returned. Durable dedupe per organization, UTC month, metric and threshold. Explicit provider rate limits and missing email configuration may be retried. A crash or ambiguous provider acceptance remains unknown and is never automatically resent: Cloudflare Email Sending has no confirmed idempotency contract here. Accepted means provider acceptance, not inbox delivery.");
        Tags("Email");
        Metadata(new AcceptsMetadata(["application/json"], typeof(AllowanceEmailRequest)), new RequiresSecurity(SecuritySchemes.ClientBasic));
        Produces<AllowanceEmailResponse>();
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status422UnprocessableEntity);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }
        var request = await EmailApi.ReadAsync<AllowanceEmailRequest>(this.HttpContext, cancellationToken).ConfigureAwait(false);
        var month = clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (!TerraScaleIds.IsValid(request.OrganizationId) || request.BillingMonth != month || request.Metric is not ("operations" or "storage") ||
            request.Threshold is not (80 or 100) || request.Used < 0 || request.Allowance <= 0 ||
            (decimal)request.Used * 100 < (decimal)request.Allowance * request.Threshold)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "invalid_request", "An observed current-month allowance threshold is required.");
        }
        if (await directory.OrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw PlatformDirectory.NotFound();
        }

        var result = await deliveries.SendAsync("allowance", $"{request.OrganizationId}/{month}/{request.Metric}/{request.Threshold}", request.OrganizationId!, Message(request),
            cancellationToken).ConfigureAwait(false);
        return TypedResults.Json(new AllowanceEmailResponse(result), AllowanceEmailJson.Default.AllowanceEmailResponse);
    }

    private static EmailMessage Message(AllowanceEmailRequest request)
    {
        var name = request.Metric == "operations" ? "Operations" : "Storage";
        var subject = $"{name} allowance: {request.Threshold}% reached";
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Your organization has reached {request.Threshold}% of its {name.ToLowerInvariant()} allowance for {request.BillingMonth}. Observed usage: {request.Used:N0} of {request.Allowance:N0}. Review usage and plans at https://dash.terrascale.tech/usage?organizationId={request.OrganizationId} .");
        return new(subject, text) { FromName = "TerraScale", Purpose = EmailPurpose.Allowance, Tenant = request.OrganizationId! };
    }

}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AllowanceEmailRequest))]
[JsonSerializable(typeof(AllowanceEmailResponse))]
internal sealed partial class AllowanceEmailJson : JsonSerializerContext;
