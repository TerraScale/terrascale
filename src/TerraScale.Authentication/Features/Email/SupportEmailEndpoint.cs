using Dapper;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Email;

public sealed record EmailSupportRequest(string RequestId, string UserId, string OrganizationId, string? ProjectId, string? DatabaseId,
    string Plan, string Severity, string Subject, string Message);

public sealed record EmailSupportResponse(string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AcknowledgementStatus = null);

public sealed record SupportEmailDelivery(string InstanceId, string RequestId, string UserId, string Status, string? AcknowledgementStatus = null)
{
    public string? Version { get; init; }
}

public sealed class SupportEmailEndpoint(AuthData data, PlatformDirectory directory, Users users, IEmailSender sender, AttemptLimiter limiter, AuthOptions options, TimeProvider clock)
    : EndpointWithoutRequest<IResult>
{
    public const string SupportAddress = "support@terrascale.tech";
    public const int PerUserPerHour = 5;

    private static readonly Dictionary<string, string> Targets = new(StringComparer.Ordinal)
    {
        ["free"] = "Free plan requests are answered best effort, with no response target.",
        ["starter"] = "Starter plan target: first response within 2 business days.",
        ["pro"] = "Pro plan target: first response within 1 business day.",
        ["team"] = "Team plan target: first response within 8 business hours.",
        ["enterprise"] = "Enterprise target: the first-response time in your contract.",
    };

    public override void Configure()
    {
        AuthenticateInHandler();
        Post("/v1/{slug}/email/support");
        Name("email_support_send");
        Summary("Send a customer support request");
        Description("Dashboard ClientBasic email authority. ts-auth checks the user's access to the organization and project, sends the request to support@terrascale.tech with Reply-To set to the user's address, persists acceptance, then attempts an acknowledgement. Status describes the support message; optional acknowledgementStatus describes the separate acknowledgement. Repeats return recorded outcomes without resending either message, except a retryable support send releases the id for a safe retry. Unknown outcomes are never retried. 429 rate_limited past 5 requests per user per hour. Accepted means provider acceptance, not inbox delivery.");
        Tags("Email");
        Metadata(new AcceptsMetadata(["application/json"], typeof(EmailSupportRequest)), new RequiresSecurity(SecuritySchemes.ClientBasic));
        Produces<EmailSupportResponse>();
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status422UnprocessableEntity);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }
        return (await EmailApi.ReadAsync<EmailSupportRequest>(this.HttpContext, cancellationToken) switch
        {
            EmailSupportRequest request => await SendAsync(request, cancellationToken),
            AuthValidation error => ErrorResults.ToResult(error),
            AuthPayloadTooLarge error => ErrorResults.ToResult(error),
            AuthUnsupportedMediaType error => ErrorResults.ToResult(error),
        });
    }

    private async Task<IResult> SendAsync(EmailSupportRequest request, CancellationToken cancellationToken)
    {
        var subject = request.Subject?.Trim() ?? "";
        var message = request.Message?.Trim() ?? "";
        if (!TerraScaleIds.IsValid(request.RequestId) || !TerraScaleIds.IsValid(request.UserId) || !TerraScaleIds.IsValid(request.OrganizationId) ||
            request.ProjectId is { } project && !TerraScaleIds.IsValid(project) || request.DatabaseId is { } database && !TerraScaleIds.IsValid(database) ||
            request.DatabaseId is not null && request.ProjectId is null || !Targets.ContainsKey(request.Plan ?? "") ||
            request.Severity is not ("low" or "normal" or "high" or "urgent") ||
            subject.Length is 0 or > 150 || subject.Any(char.IsControl) || message.Length is 0 or > 8000)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "invalid_request", "A severity, a one-line subject of at most 150 characters and a message of at most 8000 characters are required.");
        }

        var user = await users.GetAsync(OidcIssuer.InstanceId, request.UserId, cancellationToken) ?? throw PlatformDirectory.NotFound();
        var (organizationName, projectName) = request.ProjectId is null
            ? ((await directory.OrganizationAccessAsync(user.Id, request.OrganizationId, cancellationToken)).Organization.Value.Name, null)
            : await ProjectNamesAsync(user.Id, request, cancellationToken);

        if (await this.ReadAsync(request, cancellationToken) is { } recorded)
        {
            return Answer(recorded.Delivery);
        }
        if (!await limiter.TryAcquireAsync(OidcIssuer.InstanceId, "support", user.Id, PerUserPerHour, TimeSpan.FromHours(1), cancellationToken))
        {
            return ErrorResults.ToResult(new AuthQuotaExceeded("Too many support requests; retry within the hour or email support@terrascale.tech.", "rate_limited"));
        }
        var delivery = new SupportEmailDelivery(OidcIssuer.InstanceId, request.RequestId, user.Id, "sending");
        var reserved = Change.Insert(delivery);
        if (!await data.SaveAsync(reserved, cancellationToken))
        {
            return Answer((await this.ReadAsync(request, cancellationToken))?.Delivery ?? throw ApiException.Unavailable());
        }

        var version = reserved.Version!;

        var reference = request.RequestId[^8..];
        var details = new EmailMessage($"[{request.Severity}] {subject} ({reference})", this.Details(request, user.Email, organizationName, projectName, subject, message))
        {
            FromName = "TerraScale support form",
            Purpose = EmailPurpose.Support,
            Tenant = request.OrganizationId!,
            ReplyTo = user.Email,
        };
        var outcome = await sender.DeliverAsync(SupportAddress, details, cancellationToken);
        delivery = delivery with { Status = Status(outcome), AcknowledgementStatus = outcome == EmailDeliveryOutcome.Accepted ? "sending" : null };
        if (outcome == EmailDeliveryOutcome.Retryable)
        {
            if (!await data.SaveAsync(Change.Delete(delivery, version), cancellationToken))
            {
                throw ApiException.Unavailable();
            }
            return Answer(delivery);
        }

        var accepted = Change.Update(delivery, version);
        version = await data.SaveAsync(accepted, cancellationToken) ? accepted.Version! : throw ApiException.Unavailable();
        if (outcome == EmailDeliveryOutcome.Accepted)
        {
            EmailDeliveryOutcome acknowledgement;
            try
            {
                var acknowledgementMessage = Acknowledgement(request, reference, subject, message) with { Purpose = EmailPurpose.Support, Tenant = request.OrganizationId! };
                acknowledgement = await sender.DeliverAsync(user.Email, acknowledgementMessage, cancellationToken);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                acknowledgement = EmailDeliveryOutcome.Unknown;
            }
            delivery = delivery with { AcknowledgementStatus = Status(acknowledgement) };
            if (!await data.SaveAsync(Change.Update(delivery, version), cancellationToken))
            {
                throw ApiException.Unavailable();
            }
        }
        return Answer(delivery);
    }

    private async Task<(string Organization, string? Project)> ProjectNamesAsync(string userId, EmailSupportRequest request, CancellationToken cancellationToken)
    {
        var access = await directory.ProjectAccessAsync(userId, request.OrganizationId, request.ProjectId!, cancellationToken);
        return (access.Organization.Value.Name, access.Project.Value.Name);
    }

    private static string Status(EmailDeliveryOutcome outcome) => outcome switch
    {
        EmailDeliveryOutcome.Accepted => "accepted",
        EmailDeliveryOutcome.Retryable => "retryable",
        EmailDeliveryOutcome.Rejected => "rejected",
        _ => "unknown",
    };

    private static JsonHttpResult<EmailSupportResponse> Answer(SupportEmailDelivery delivery) =>
        TypedResults.Json(new EmailSupportResponse(delivery.Status == "sending" ? "unknown" : delivery.Status,
            delivery.AcknowledgementStatus == "sending" ? "unknown" : delivery.AcknowledgementStatus), SupportEmailJson.Default.EmailSupportResponse);

    private string Details(EmailSupportRequest request, string email, string organizationName, string? projectName, string subject, string message)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Support request {request.RequestId}\n");
        text.Append(CultureInfo.InvariantCulture, $"Severity: {request.Severity}\nPlan: {request.Plan}\n");
        text.Append(CultureInfo.InvariantCulture, $"From: {email} (user {request.UserId})\n");
        text.Append(CultureInfo.InvariantCulture, $"Organization: {organizationName} ({request.OrganizationId})\n");
        text.Append(CultureInfo.InvariantCulture, $"Project: {(request.ProjectId is null ? "none" : $"{projectName} ({request.ProjectId})")}\n");
        text.Append(CultureInfo.InvariantCulture, $"Database: {request.DatabaseId ?? "none"}\n");
        text.Append(CultureInfo.InvariantCulture, $"Submitted: {clock.GetUtcNow():yyyy-MM-dd HH:mm:ss} UTC\n");
        text.Append(CultureInfo.InvariantCulture, $"Subject: {subject}\n\n{message}\n\nReply to this email to answer the customer.\n");
        return text.ToString();
    }

    private static EmailMessage Acknowledgement(EmailSupportRequest request, string reference, string subject, string message) => new(
        $"We received your support request ({reference})",
        $"Thanks for contacting TerraScale support. We received your request and will reply to this address.\n\nReference: {request.RequestId}\nSeverity: {request.Severity}\nSubject: {subject}\n\n{Targets[request.Plan]} Business hours are Monday to Friday, 09:00 to 18:00 CET.\n\nReply to this email to add details. Never send passwords, API keys or customer data. Service status: https://status.terrascale.tech\n\nYour message:\n{message}\n")
    { FromName = "TerraScale support", ReplyTo = SupportAddress };

    private async Task<Versioned<SupportEmailDelivery>?> ReadStoredAsync(string requestId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<SupportEmailDelivery>(
            "SELECT * FROM support_deliveries WHERE request_id = @requestId", new { requestId }), cancellationToken));

    private async Task<(SupportEmailDelivery Delivery, string Version)?> ReadAsync(EmailSupportRequest request, CancellationToken cancellationToken) =>
        await this.ReadStoredAsync(request.RequestId, cancellationToken) is not { } stored ? null
        : stored.Value.UserId == request.UserId ? (stored.Value, stored.Version)
        : throw PlatformDirectory.NotFound();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EmailSupportRequest))]
[JsonSerializable(typeof(EmailSupportResponse))]
internal sealed partial class SupportEmailJson : JsonSerializerContext;
