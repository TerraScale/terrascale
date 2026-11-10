using System.Security.Cryptography;
using System.Text;
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
    Accepted,
    Retryable,
    Rejected,
    Unknown,
}

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
        try
        {
            logger.ZLogWarning($"Email not delivered: no SMTP provider is configured (recipient {recipient.Length:@RecipientLength}, subject {message.Subject.Length:@SubjectLength}, text {message.Text.Length:@TextLength}, HTML {(message.Html?.Length ?? 0):@HtmlLength} characters)");
        }
        catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
        {
            _ = logFailure;
        }

        return Task.CompletedTask;
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
