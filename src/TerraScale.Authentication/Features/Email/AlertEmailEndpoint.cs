using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

/// <summary>A dashboard alert rule transition, without a caller-selected recipient.</summary>
/// <param name="OrganizationId">Organization whose current owners receive the message.</param>
/// <param name="ProjectId">Project of that organization the rule watches.</param>
/// <param name="AlertId">The alert rule's ULID.</param>
/// <param name="State">firing or resolved.</param>
/// <param name="RuleName">Rule name, at most 100 characters.</param>
/// <param name="Metric">p95_latency_ms, error_rate_percent, storage_gb or operation_units_percent.</param>
/// <param name="DatabaseName">The watched database, or null for the whole project.</param>
/// <param name="Threshold">The rule's threshold.</param>
/// <param name="Value">The observed value.</param>
public sealed record EmailAlertRequest(string? OrganizationId, string? ProjectId, string? AlertId, string? State, string? RuleName, string? Metric,
    string? DatabaseName, double? Threshold, double? Value);

internal sealed record AlertEmailDelivery(string InstanceId, string OrganizationId, string AlertId, string State, Dictionary<string, string> Recipients);

/// <summary>Delivers an alert transition through the dashboard's existing ClientBasic email authority, as allowance messages are.</summary>
public sealed partial class AlertEmailEndpoint(AuthStore store, PlatformDirectory directory, Users users, IEmailSender sender, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    private const string Collection = "email_alert_deliveries";

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/{slug}/email/alert");
        Name("email_alert_send");
        Summary("Notify the current organization owners of an alert transition");
        Description("Dashboard ClientBasic email authority. ts-auth resolves current owners internally; no recipient identities are returned. Durable dedupe per organization, alert and state, so one message per transition. Delivery status semantics match email_allowance_send.");
        Tags("Email");
        Metadata(new AcceptsMetadata(["application/json"], typeof(EmailAlertRequest)), new RequiresSecurity(SecuritySchemes.ClientBasic));
        Produces<AllowanceEmailResponse>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }
        var request = await EmailApi.ReadAsync<EmailAlertRequest>(this.HttpContext, cancellationToken).ConfigureAwait(false);
        if (!TerraScaleIds.IsValid(request.OrganizationId) || !TerraScaleIds.IsValid(request.ProjectId) || !TerraScaleIds.IsValid(request.AlertId) ||
            request.State is not ("firing" or "resolved") || request.Metric is not ("p95_latency_ms" or "error_rate_percent" or "storage_gb" or "operation_units_percent") ||
            request.RuleName?.Trim() is not { Length: > 0 and <= 100 } name || name.Any(char.IsControl) ||
            request.DatabaseName is { } database && !DatabaseName().IsMatch(database) ||
            request.Threshold is not { } threshold || !double.IsFinite(threshold) || request.Value is not { } value || !double.IsFinite(value))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request",
                "organizationId, projectId and alertId must be ULIDs; state firing or resolved; ruleName 1 to 100 characters; a known metric; databaseName null or a database name; threshold and value finite numbers.");
        }
        var organization = request.OrganizationId!;
        if (await directory.OrganizationAsync(organization, cancellationToken).ConfigureAwait(false) is null ||
            await directory.ProjectAsync(request.ProjectId!, cancellationToken).ConfigureAwait(false) is not { Value: var project } || project.OrganizationId != organization)
        {
            throw PlatformDirectory.NotFound();
        }

        var key = $"{organization}/{request.AlertId}/{request.State}";
        if (await store.GetAsync(Collection, OidcIssuer.InstanceId, key, cancellationToken).ConfigureAwait(false) is null)
        {
            var recipients = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (var document in store.QueryAsync(AuthStore.MembershipCollection, OidcIssuer.InstanceId,
                [("/organizationId", organization), ("/role", Roles.Owner)], cancellationToken).ConfigureAwait(false))
            {
                if (AllowanceEmailEndpoint.ReadMember(document, organization) is { Role: Roles.Owner } member)
                {
                    recipients[member.UserId] = "pending";
                }
                if (recipients.Count > 100)
                {
                    throw ApiException.Unavailable();
                }
            }
            try
            {
                _ = await store.CreateAsync(Collection, OidcIssuer.InstanceId, key, Serialize(new(OidcIssuer.InstanceId, organization, request.AlertId!, request.State, recipients)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TerraBasePreconditionFailedException) { }
        }

        var message = Message(request, name, project.Name, threshold, value);
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
                if (await store.ReplaceAsync(Collection, OidcIssuer.InstanceId, key, Serialize(current.Delivery), current.Version, cancellationToken).ConfigureAwait(false) is null)
                {
                    continue;
                }

                // A fresh membership read prevents sending to someone removed since the delivery was reserved.
                var membership = await store.GetAsync(AuthStore.MembershipCollection, OidcIssuer.InstanceId, $"{organization}/{recipient}", cancellationToken).ConfigureAwait(false);
                var user = await users.GetAsync(OidcIssuer.InstanceId, recipient, cancellationToken).ConfigureAwait(false);
                var outcome = membership is not null && AllowanceEmailEndpoint.ReadMember(membership, organization).Role == Roles.Owner && user is not null
                    ? await sender.DeliverAsync(user.Email, message, cancellationToken).ConfigureAwait(false)
                    : EmailDeliveryOutcome.Rejected;
                await CompleteAsync(key, request, recipient, outcome switch
                {
                    EmailDeliveryOutcome.Accepted => "accepted",
                    EmailDeliveryOutcome.Retryable => "pending",
                    EmailDeliveryOutcome.Rejected => "rejected",
                    _ => "unknown",
                }, cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        var states = (await ReadAsync(key, request, cancellationToken).ConfigureAwait(false)).Delivery.Recipients.Values;
        var result = states.Count == 0 ? "no_recipients" : states.Any(state => state is "unknown" or "sending") ? "unknown"
            : states.Contains("pending") ? "retryable" : states.Contains("rejected") ? "rejected" : "accepted";
        return TypedResults.Json(new AllowanceEmailResponse(result), AlertEmailJson.Default.AllowanceEmailResponse);
    }

    private static EmailMessage Message(EmailAlertRequest request, string name, string project, double threshold, double value)
    {
        var (metric, unit) = request.Metric switch
        {
            "p95_latency_ms" => ("p95 latency", " ms"),
            "error_rate_percent" => ("Error rate", "%"),
            "storage_gb" => ("Storage", " GB"),
            _ => ("Operation units used", "% of the allowance"),
        };
        var firing = request.State == "firing";
        var scope = request.DatabaseName is { } database ? $"database {database} in project {project}" : $"project {project}";
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Your alert rule \"{name}\" {(firing ? "is firing" : "has resolved")} for {scope}.\n\n{metric}: {value:0.##}{unit} (threshold {threshold:0.##}{unit}).\n\nView and manage alerts at https://dashboard.terrascale.tech/notifications/alerts .");
        return new($"[TerraScale] Alert {(firing ? "firing" : "resolved")}: {name}", text) { FromName = "TerraScale" };
    }

    private static JsonElement Serialize(AlertEmailDelivery delivery) => JsonSerializer.SerializeToElement(delivery, AlertEmailJson.Default.AlertEmailDelivery);

    private async Task<(AlertEmailDelivery Delivery, string Version)> ReadAsync(string key, EmailAlertRequest request, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(Collection, OidcIssuer.InstanceId, key, cancellationToken).ConfigureAwait(false) ?? throw ApiException.Unavailable();
        var delivery = document.Document.Deserialize(AlertEmailJson.Default.AlertEmailDelivery);
        if (document.State != "value" || document.Id != AuthStore.NaturalKeyId(OidcIssuer.InstanceId, Collection, key) || delivery is null ||
            delivery.InstanceId != OidcIssuer.InstanceId || delivery.OrganizationId != request.OrganizationId || delivery.AlertId != request.AlertId ||
            delivery.State != request.State || delivery.Recipients is null)
        {
            throw ApiException.Unavailable();
        }
        return (delivery, document.Version);
    }

    private async Task CompleteAsync(string key, EmailAlertRequest request, string recipient, string status, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await ReadAsync(key, request, cancellationToken).ConfigureAwait(false);
            current.Delivery.Recipients[recipient] = status;
            if (await store.CompareAndPutAsync(Collection, OidcIssuer.InstanceId, key, Serialize(current.Delivery), current.Version, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        throw ApiException.Unavailable();
    }

    // TerraBase database names.
    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseName();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EmailAlertRequest))]
[JsonSerializable(typeof(AllowanceEmailResponse))]
[JsonSerializable(typeof(AlertEmailDelivery))]
internal sealed partial class AlertEmailJson : JsonSerializerContext;
