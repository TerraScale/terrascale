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

public sealed record EmailMessage(string Subject, string Text, string? Html = null)
{
    public EmailPurpose Purpose { get; init; } = EmailPurpose.Notification;

    public string Tenant { get; init; } = "platform";

    public string? FromName { get; init; }

    public string? ReplyTo { get; init; }

    public override string ToString() => nameof(EmailMessage);
}

public interface IEmailSender
{
    Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken);

    async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        await this.SendAsync(recipient, message, cancellationToken);
        return EmailDeliveryOutcome.Unknown;
    }
}

public enum EmailDeliveryOutcome
{
    Accepted,
    Retryable,
    Rejected,
    Unknown,
}

public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        await this.SendAsync(recipient, message, cancellationToken);
        return EmailDeliveryOutcome.Retryable;
    }

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

public sealed class CloudflareEmailSender(HttpClient http, CloudflareEmailOptions email, ILogger<CloudflareEmailSender> logger) : IEmailSender, IEmailProvider
{
    private const string SenderAddress = "no-reply@terrascale.tech";

    public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) =>
        _ = await this.DeliverAsync(recipient, message, cancellationToken);

    public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        => (await this.DeliverResultAsync(recipient, message, cancellationToken)).Outcome;

    public async Task<EmailProviderResult> DeliverResultAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
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
                outcome = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "429" : "error";
                this.LogProviderFailure((int)response.StatusCode);
                return response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? new EmailProviderResult(EmailDeliveryOutcome.Retryable,
                    response.Headers.RetryAfter?.Date, response.Headers.RetryAfter?.Delta)
                    : (int)response.StatusCode < 500 ? EmailDeliveryOutcome.Rejected : EmailDeliveryOutcome.Unknown;
            }

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
            cancellationToken.ThrowIfCancellationRequested();
            AuthMetrics.Email("failed");

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

internal sealed record CloudflareSendRequest(
    [property: JsonPropertyName("from")] CloudflareSender From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("html")] string? Html,
    [property: JsonPropertyName("reply_to")] string? ReplyTo);

internal sealed record CloudflareSender([property: JsonPropertyName("address")] string Address, [property: JsonPropertyName("name")] string? Name);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CloudflareSendRequest))]
internal sealed partial class CloudflareEmailJson : JsonSerializerContext;

public sealed record EmailProviderResult(EmailDeliveryOutcome Outcome, DateTimeOffset? Reset = null, TimeSpan? ResetDelay = null)
{
    public static implicit operator EmailProviderResult(EmailDeliveryOutcome outcome)
    {
        return new(outcome);
    }
}

public enum EmailPurpose
{
    Authentication = 0,
    Invitation = 1,
    Alert = 2,
    Allowance = 3,
    Support = 4,
    Test = 5,
    Notification = 6,
}
