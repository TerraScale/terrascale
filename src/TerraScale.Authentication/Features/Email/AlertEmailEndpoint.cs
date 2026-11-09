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
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Email;

public sealed record EmailAlertRequest(string? OrganizationId, string? ProjectId, string? AlertId, string? State, string? RuleName, string? Metric,
    string? DatabaseName, double? Threshold, double? Value);

public sealed partial class AlertEmailEndpoint(PlatformDirectory directory, OwnerDeliveries deliveries, AuthOptions options) : EndpointWithoutRequest<IResult>
{

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

        var result = await deliveries.SendAsync("alert", $"{organization}/{request.AlertId}/{request.State}", organization, Message(request, name, project.Name, threshold, value),
            cancellationToken).ConfigureAwait(false);
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
            $"Your alert rule \"{name}\" {(firing ? "is firing" : "has resolved")} for {scope}.\n\n{metric}: {value:0.##}{unit} (threshold {threshold:0.##}{unit}).\n\nView and manage alerts at https://dash.terrascale.tech/notifications/alerts .");
        return new($"[TerraScale] Alert {(firing ? "firing" : "resolved")}: {name}", text) { FromName = "TerraScale", Purpose = EmailPurpose.Alert, Tenant = request.OrganizationId! };
    }

    // TerraBase database names.
    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseName();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EmailAlertRequest))]
[JsonSerializable(typeof(AllowanceEmailResponse))]
internal sealed partial class AlertEmailJson : JsonSerializerContext;
