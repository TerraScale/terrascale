using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Hosting;
using ZLogger;

namespace TerraScale.Authentication.Features.Email;

/// <summary>An email to send: a plain-text body and, optionally, an HTML alternative of the same content.</summary>
/// <param name="Subject">The subject line.</param>
/// <param name="Text">The plain-text body.</param>
/// <param name="Html">The HTML body, or <see langword="null"/> for a plain-text message.</param>
public sealed record EmailMessage(string Subject, string Text, string? Html = null)
{
    /// <summary>Server-assigned mail class. Unclassified mail never spends the authentication reserve.</summary>
    public EmailPurpose Purpose { get; init; } = EmailPurpose.Notification;

    /// <summary>Server-resolved tenant identity for bounded per-tenant admission.</summary>
    public string Tenant { get; init; } = "platform";

    /// <summary>Display name of the From address; the address itself is always the configured one.</summary>
    public string? FromName { get; init; }

    /// <summary>A plain Reply-To address, if replies should go somewhere.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>Omits the content, so formatting or logging the message never discloses a code or a link.</summary>
    public override string ToString() => nameof(EmailMessage);
}

/// <summary>
/// Sends transactional email. SendAsync swallows provider failures; DeliverAsync reports acceptance
/// for private APIs and durable ledgers. Public authentication flows preserve anti-enumeration. Only caller cancellation propagates.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends <paramref name="message"/> to <paramref name="recipient"/>.</summary>
    Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken);

    /// <summary>Reports provider acceptance for durable product deliveries; legacy senders cannot confirm acceptance.</summary>
    async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        await this.SendAsync(recipient, message, cancellationToken).ConfigureAwait(false);
        return EmailDeliveryOutcome.Unknown;
    }
}

/// <summary>The provider outcome; Unknown must never be automatically retried without provider idempotency.</summary>
public enum EmailDeliveryOutcome
{
    /// <summary>Cloudflare accepted the message, which does not prove inbox delivery.</summary>
    Accepted,
    /// <summary>No send occurred, or a rate limit explicitly refused it; a later attempt is safe.</summary>
    Retryable,
    /// <summary>The provider permanently refused this message.</summary>
    Rejected,
    /// <summary>Acceptance is ambiguous, so automatic retry could send a duplicate.</summary>
    Unknown,
}

/// <summary>
/// Used when Cloudflare Email Sending is not configured: nothing is delivered, and the warning records only
/// message sizes, never the recipient or the content (which carries links and codes).
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    /// <inheritdoc />
    public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        await this.SendAsync(recipient, message, cancellationToken).ConfigureAwait(false);
        return EmailDeliveryOutcome.Retryable;
    }

    /// <inheritdoc />
    public Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(message);
        EmailOutcomeLog.Write(logger, recipient, "error");
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        AuthMetrics.Email("not_configured");
        // Best effort, like the Cloudflare sender's failure log: a failing provider must not fail the flow.
        try
        {
            logger.ZLogWarning($"Email not delivered: Cloudflare Email Sending is not configured (recipient {recipient.Length:@RecipientLength}, subject {message.Subject.Length:@SubjectLength}, text {message.Text.Length:@TextLength}, HTML {(message.Html?.Length ?? 0):@HtmlLength} characters)");
        }
        catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
        {
            _ = logFailure;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Sends through Cloudflare Email Sending (<c>POST /accounts/{id}/email/sending/send</c>) over HTTPS with the
/// account's token. Registered only when <see cref="AuthOptions.CloudflareEmail"/> is set. The From address is
/// fixed: it is the domain the account sends for.
/// </summary>
public sealed class CloudflareEmailSender(HttpClient http, CloudflareEmailOptions email, ILogger<CloudflareEmailSender> logger) : IEmailSender
{
    private const string SenderAddress = "no-reply@terrascale.tech";

    /// <inheritdoc />
    public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) =>
        _ = await this.DeliverAsync(recipient, message, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        => (await this.DeliverResultAsync(recipient, message, cancellationToken).ConfigureAwait(false)).Outcome;

    internal async Task<EmailProviderResult> DeliverResultAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        var outcome = "error";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.cloudflare.com/client/v4/accounts/{email.AccountId}/email/sending/send")
            {
                Content = JsonContent.Create(
                    new CloudflareSendRequest(new CloudflareSender(SenderAddress, message.FromName), recipient, message.Subject, message.Text, message.Html, message.ReplyTo),
                    CloudflareEmailJson.Default.CloudflareSendRequest),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", email.ApiToken);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                AuthMetrics.Email("failed");
                // The response body can quote the recipient, so only the status is logged.
                outcome = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "429" : "error";
                this.LogProviderFailure((int)response.StatusCode);
                return response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? new EmailProviderResult(EmailDeliveryOutcome.Retryable,
                    response.Headers.RetryAfter?.Date, response.Headers.RetryAfter?.Delta)
                    : (int)response.StatusCode < 500 ? EmailDeliveryOutcome.Rejected : EmailDeliveryOutcome.Unknown;
            }

            // HTTP success alone does not confirm acceptance of this recipient.
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !UniqueProperties(root))
            {
                AuthMetrics.Email("failed");
                return EmailDeliveryOutcome.Unknown;
            }
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                AuthMetrics.Email("failed");
                return EmailDeliveryOutcome.Rejected;
            }
            var hasResult = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object;
            if (!hasResult || !UniqueProperties(result) || !ValidRecipientLists(result))
            {
                AuthMetrics.Email("failed");
                return EmailDeliveryOutcome.Unknown;
            }
            if (NonemptyArray(result, "permanent_bounces") || NonemptyArray(result, "suppressed_recipients"))
            {
                AuthMetrics.Email("bounced");
                outcome = "bounced";
                this.LogProviderFailure(null);
                return EmailDeliveryOutcome.Rejected;
            }

            if (success.ValueKind != JsonValueKind.True
                || !(ContainsRecipient(result, "queued", recipient) || ContainsRecipient(result, "delivered", recipient)))
            {
                AuthMetrics.Email("failed");
                return EmailDeliveryOutcome.Unknown;
            }

            AuthMetrics.Email("sent");
            outcome = "queued";
            return EmailDeliveryOutcome.Accepted;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Caller cancellation wins over whatever failure it raced with; every other failure
            // is a swallowed delivery failure.
            cancellationToken.ThrowIfCancellationRequested();
            AuthMetrics.Email("failed");

            // The safe logger keeps only the exception type and frames. Logging is best effort: a failing
            // provider must not turn a swallowed delivery failure into a failed request.
            try
            {
                logger.ZLogError(exception, $"Email delivery failed");
            }
            catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
            {
                _ = logFailure;
            }
            return EmailDeliveryOutcome.Unknown;
        }
        finally
        {
            EmailOutcomeLog.Write(logger, recipient, outcome);
        }
    }

    private static bool NonemptyArray(JsonElement result, string name) =>
        result.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0;

    private static bool UniqueProperties(JsonElement value) =>
        value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();

    private static bool ValidRecipientLists(JsonElement result)
    {
        foreach (var name in new[] { "queued", "delivered", "permanent_bounces", "suppressed_recipients" })
        {
            if (result.TryGetProperty(name, out var list)
                && (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ContainsRecipient(JsonElement result, string name, string recipient) =>
        result.TryGetProperty(name, out var list) && list.EnumerateArray().Any(value => value.ValueEquals(recipient));

    private void LogProviderFailure(int? status)
    {
        try
        {
            if (status is { } code)
            {
                logger.ZLogError($"Email delivery failed: Cloudflare answered {code:@Status}");
            }
            else
            {
                logger.ZLogError($"Email delivery failed: Cloudflare reported a permanent bounce or suppression");
            }
        }
        catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
        {
            _ = logFailure;
        }
    }
}

/// <summary>Best-effort outcomes contain only a recipient digest and a fixed outcome label.</summary>
internal static class EmailOutcomeLog
{
    public static void Write(ILogger logger, string recipient, string outcome)
    {
        try
        {
            var recipientHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(recipient)));
            logger.ZLogInformation($"Email send outcome: {outcome:@Outcome} (recipient SHA256 {recipientHash:@RecipientHash})");
        }
        catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
        {
            _ = logFailure;
        }
    }
}

/// <summary>The body of Cloudflare's send request. Unset optional fields are omitted, since <c>html</c> must be a string.</summary>
internal sealed record CloudflareSendRequest(
    [property: JsonPropertyName("from")] CloudflareSender From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("html")] string? Html,
    [property: JsonPropertyName("reply_to")] string? ReplyTo);

/// <summary>A sender as Cloudflare's address object; a null name sends the address alone.</summary>
internal sealed record CloudflareSender([property: JsonPropertyName("address")] string Address, [property: JsonPropertyName("name")] string? Name);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CloudflareSendRequest))]
internal sealed partial class CloudflareEmailJson : JsonSerializerContext;

internal sealed record EmailProviderResult(EmailDeliveryOutcome Outcome, DateTimeOffset? Reset = null, TimeSpan? ResetDelay = null)
{
    public static implicit operator EmailProviderResult(EmailDeliveryOutcome outcome)
    {
        return new(outcome);
    }
}

/// <summary>Fixed server-owned mail purposes.</summary>
public enum EmailPurpose
{
    /// <summary>Verification, sign-in, MFA or password recovery.</summary>
    Authentication = 0,
    /// <summary>Organization invitation.</summary>
    Invitation = 1,
    /// <summary>Usage or operational alert.</summary>
    Alert = 2,
    /// <summary>Allowance notification.</summary>
    Allowance = 3,
    /// <summary>Support request or acknowledgement.</summary>
    Support = 4,
    /// <summary>Template test traffic.</summary>
    Test = 5,
    /// <summary>Other noncritical notification.</summary>
    Notification = 6,
}
