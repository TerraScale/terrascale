using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Email;

/// <summary>Reserves one infrastructure notification attempt, never authentication capacity.</summary>
public sealed class EmailAdmissionEndpoint(AuthOptions options, EmailAdmission admission, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/email/admission");
        Name("email_admission");
        Summary("Reserve one infrastructure notification attempt");
        Description("ts-alerts only. Domain-separated HMAC credential derived from the shared Cloudflare token. A fresh ULID may grant one send, within ten seconds. Admitted IDs never grant another attempt; refusals and ambiguous responses suppress email. No retry of an unknown provider outcome.");
        Tags("Email");
        Metadata(new AcceptsMetadata(["application/json"], typeof(EmailAdmissionRequest)), new RequiresSecurity(SecuritySchemes.MailAdmission));
        Produces<EmailAdmissionResponse>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status415UnsupportedMediaType);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var authorization = this.HttpContext.Request.Headers.Authorization.ToString();
        var expected = options.CloudflareEmail is { } email ? Credential(email.ApiToken) : string.Empty;
        if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(authorization)), SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + expected))))
        {
            return TypedResults.Unauthorized();
        }
        var request = await EmailApi.ReadAsync<EmailAdmissionRequest>(this.HttpContext, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (request is null || !NUlid.Ulid.TryParse(request.AttemptId, out var id) || id.Time < now - TimeSpan.FromMinutes(5) || id.Time > now + TimeSpan.FromMinutes(1))
        {
            return EmailApi.Error(StatusCodes.Status400BadRequest, "invalid_request", "A fresh attempt ULID is required.");
        }
        if (!await admission.AcquireAsync(new("", "") { Purpose = EmailPurpose.Alert, Tenant = "ts-alerts" }, cancellationToken, id.ToString()).ConfigureAwait(false))
        {
            this.HttpContext.Response.Headers.RetryAfter = "60";
            return EmailApi.Error(StatusCodes.Status429TooManyRequests, "rate_limited", "Notification capacity is unavailable.");
        }
        return TypedResults.Json(new EmailAdmissionResponse("admitted", now + TimeSpan.FromSeconds(10)), EmailAdmissionApiJson.Default.EmailAdmissionResponse);
    }

    internal static string Credential(string token) => Convert.ToHexStringLower(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(token), "terrascale:email-admission:v1:ts-alerts"u8));
}

internal sealed record EmailAdmissionRequest(string AttemptId);
internal sealed record EmailAdmissionResponse(string Status, DateTimeOffset SendBy);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EmailAdmissionRequest))]
[JsonSerializable(typeof(EmailAdmissionResponse))]
internal sealed partial class EmailAdmissionApiJson : JsonSerializerContext;
