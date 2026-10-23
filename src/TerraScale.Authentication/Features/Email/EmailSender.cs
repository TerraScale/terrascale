using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    /// <summary>Display name of the From address; the address itself is always the configured one.</summary>
    public string? FromName { get; init; }

    /// <summary>A plain Reply-To address, if replies should go somewhere.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>Omits the content, so formatting or logging the message never discloses a code or a link.</summary>
    public override string ToString() => nameof(EmailMessage);
}

/// <summary>
/// Sends transactional email. Delivery failures are logged and swallowed, so a flow's outcome
/// never reveals whether mail could be delivered. Only caller cancellation propagates.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends <paramref name="message"/> to <paramref name="recipient"/>.</summary>
    Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Used when Cloudflare Email Sending is not configured: nothing is delivered, and the warning records only
/// message sizes, never the recipient or the content (which carries links and codes).
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    /// <inheritdoc />
    public Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(message);
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
    public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
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
                logger.ZLogError($"Email delivery failed: Cloudflare answered {(int)response.StatusCode:@Status}");
                return;
            }

            // A 2xx still lists a suppressed or invalid recipient under permanent_bounces; it never arrives.
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (body.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("permanent_bounces", out var bounces) && bounces.ValueKind == JsonValueKind.Array && bounces.GetArrayLength() > 0)
            {
                AuthMetrics.Email("bounced");
                logger.ZLogError($"Email delivery failed: Cloudflare reported a permanent bounce");
                return;
            }

            AuthMetrics.Email("sent");
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
