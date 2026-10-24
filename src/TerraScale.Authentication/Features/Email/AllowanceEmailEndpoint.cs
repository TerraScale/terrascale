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
using TerraScale.Authentication.Store;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Email;

/// <summary>A product allowance alert, without a caller-selected recipient.</summary>
/// <param name="OrganizationId">Organization whose current owners receive the message.</param>
/// <param name="BillingMonth">UTC month in yyyy-MM form.</param>
/// <param name="Metric">operations or storage.</param>
/// <param name="Threshold">80 or 100.</param>
/// <param name="Used">Observed usage.</param>
/// <param name="Allowance">Published allowance.</param>
public sealed record AllowanceEmailRequest(string OrganizationId, string BillingMonth, string Metric, int Threshold, long Used, long Allowance);

/// <summary>Delivery state only; never a recipient, user identifier or address.</summary>
/// <param name="Status">accepted, retryable, unknown, rejected or no_recipients.</param>
public sealed record AllowanceEmailResponse(string Status);

internal sealed record AllowanceEmailDelivery(string InstanceId, string OrganizationId, string BillingMonth, string Metric, int Threshold,
    Dictionary<string, string> Recipients);

/// <summary>Delivers an allowance message through the dashboard's existing ClientBasic email authority.</summary>
public sealed class AllowanceEmailEndpoint(AuthStore store, PlatformDirectory directory, Users users, IEmailSender sender, AuthOptions options, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    private const string Collection = "email_allowance_deliveries";

    /// <inheritdoc />
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

    /// <inheritdoc />
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

        var key = $"{request.OrganizationId}/{month}/{request.Metric}/{request.Threshold}";
        var existing = await store.GetAsync(Collection, OidcIssuer.InstanceId, key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var recipients = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (var document in store.QueryAsync(AuthStore.MembershipCollection, OidcIssuer.InstanceId,
                [("/organizationId", request.OrganizationId), ("/role", Roles.Owner)], cancellationToken).ConfigureAwait(false))
            {
                var member = ReadMember(document, request.OrganizationId);
                if (member.Role != Roles.Owner)
                {
                    continue;
                }
                recipients[member.UserId] = "pending";
                if (recipients.Count > 100)
                {
                    throw ApiException.Unavailable();
                }
            }
            var delivery = new AllowanceEmailDelivery(OidcIssuer.InstanceId, request.OrganizationId, month, request.Metric, request.Threshold, recipients);
            try
            {
                _ = await store.CreateAsync(Collection, OidcIssuer.InstanceId, key,
                    JsonSerializer.SerializeToElement(delivery, AllowanceEmailJson.Default.AllowanceEmailDelivery), cancellationToken).ConfigureAwait(false);
            }
            catch (TerraBasePreconditionFailedException) { }
        }

        var current = await ReadAsync(key, request, cancellationToken).ConfigureAwait(false);
        foreach (var recipient in current.Delivery.Recipients.Keys)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                current = await ReadAsync(key, request, cancellationToken).ConfigureAwait(false);
                if (current.Delivery.Recipients[recipient] != "pending")
                {
                    break;
                }
                current.Delivery.Recipients[recipient] = "sending";
                var claimed = await store.ReplaceAsync(Collection, OidcIssuer.InstanceId, key,
                    JsonSerializer.SerializeToElement(current.Delivery, AllowanceEmailJson.Default.AllowanceEmailDelivery), current.Version, cancellationToken).ConfigureAwait(false);
                if (claimed is null)
                {
                    continue;
                }

                // A fresh membership read prevents sending to someone removed since the delivery was reserved.
                var membership = await store.GetAsync(AuthStore.MembershipCollection, OidcIssuer.InstanceId,
                    $"{request.OrganizationId}/{recipient}", cancellationToken).ConfigureAwait(false);
                var user = await users.GetAsync(OidcIssuer.InstanceId, recipient, cancellationToken).ConfigureAwait(false);
                var outcome = membership is not null && ReadMember(membership, request.OrganizationId).Role == Roles.Owner && user is not null
                    ? await sender.DeliverAsync(user.Email, Message(request), cancellationToken).ConfigureAwait(false)
                    : EmailDeliveryOutcome.Rejected;
                var status = outcome switch
                {
                    EmailDeliveryOutcome.Accepted => "accepted",
                    EmailDeliveryOutcome.Retryable => "pending",
                    EmailDeliveryOutcome.Rejected => "rejected",
                    _ => "unknown",
                };
                await CompleteAsync(key, request, recipient, status, cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        current = await ReadAsync(key, request, cancellationToken).ConfigureAwait(false);
        var states = current.Delivery.Recipients.Values;
        var result = states.Count == 0 ? "no_recipients" : states.Any(state => state is "unknown" or "sending") ? "unknown"
            : states.Contains("pending") ? "retryable" : states.Contains("rejected") ? "rejected" : "accepted";
        return TypedResults.Json(new AllowanceEmailResponse(result), AllowanceEmailJson.Default.AllowanceEmailResponse);
    }

    internal static MembershipRecord ReadMember(TerraBaseDocument document, string organizationId)
    {
        var member = document.Document.Deserialize(PlatformJson.Default.MembershipRecord);
        if (document.State != "value" || member is null || member.InstanceId != OidcIssuer.InstanceId || member.OrganizationId != organizationId || !TerraScaleIds.IsValid(member.UserId) ||
            document.Id != AuthStore.NaturalKeyId(OidcIssuer.InstanceId, AuthStore.MembershipCollection, $"{organizationId}/{member.UserId}"))
        {
            throw ApiException.Unavailable();
        }
        return member;
    }

    private static EmailMessage Message(AllowanceEmailRequest request)
    {
        var name = request.Metric == "operations" ? "Operations" : "Storage";
        var subject = $"{name} allowance: {request.Threshold}% reached";
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Your organization has reached {request.Threshold}% of its {name.ToLowerInvariant()} allowance for {request.BillingMonth}. Observed usage: {request.Used:N0} of {request.Allowance:N0}. Review usage and plans at https://dash.terrascale.tech/usage?organizationId={request.OrganizationId} .");
        return new(subject, text) { FromName = "TerraScale" };
    }

    private async Task<(AllowanceEmailDelivery Delivery, string Version)> ReadAsync(string key, AllowanceEmailRequest request, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(Collection, OidcIssuer.InstanceId, key, cancellationToken).ConfigureAwait(false) ?? throw ApiException.Unavailable();
        var delivery = document.Document.Deserialize(AllowanceEmailJson.Default.AllowanceEmailDelivery);
        if (document.State != "value" || document.Id != AuthStore.NaturalKeyId(OidcIssuer.InstanceId, Collection, key) ||
            delivery is null || delivery.InstanceId != OidcIssuer.InstanceId || delivery.OrganizationId != request.OrganizationId ||
            delivery.BillingMonth != request.BillingMonth || delivery.Metric != request.Metric || delivery.Threshold != request.Threshold || delivery.Recipients is null)
        {
            throw ApiException.Unavailable();
        }
        return (delivery, document.Version);
    }

    private async Task CompleteAsync(string key, AllowanceEmailRequest request, string recipient, string status, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await ReadAsync(key, request, cancellationToken).ConfigureAwait(false);
            current.Delivery.Recipients[recipient] = status;
            if (await store.CompareAndPutAsync(Collection, OidcIssuer.InstanceId, key,
                JsonSerializer.SerializeToElement(current.Delivery, AllowanceEmailJson.Default.AllowanceEmailDelivery), current.Version, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        throw ApiException.Unavailable();
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AllowanceEmailRequest))]
[JsonSerializable(typeof(AllowanceEmailResponse))]
[JsonSerializable(typeof(AllowanceEmailDelivery))]
internal sealed partial class AllowanceEmailJson : JsonSerializerContext;
