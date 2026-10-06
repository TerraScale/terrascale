using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using TerraScale.Auth.Configuration;

namespace TerraScale.Auth.Features.Email;

/// <summary>
/// Sends transactional email. Delivery failures are logged and swallowed, so a flow's outcome
/// never reveals whether mail could be delivered. Only caller cancellation propagates.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends a plain-text message.</summary>
    Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken);
}

/// <summary>
/// Used when no SMTP relay is configured: nothing is delivered, and the warning records only
/// message sizes, never the recipient or the content (which carries links and codes).
/// </summary>
public sealed partial class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    /// <inheritdoc />
    public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(body);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        // Best effort, like the SMTP sender's failure log: a failing provider must not fail the flow.
        try
        {
            LogNotDelivered(logger, recipient.Length, subject.Length, body.Length);
        }
        catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
        {
            _ = logFailure;
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email not delivered: no SMTP relay is configured (recipient {RecipientLength}, subject {SubjectLength}, body {BodyLength} characters)")]
    private static partial void LogNotDelivered(ILogger logger, int recipientLength, int subjectLength, int bodyLength);
}

/// <summary>
/// Authenticated SMTP (<c>TS_AUTH_SMTP_*</c>) with mandatory STARTTLS and certificate validation:
/// a relay that does not offer TLS never receives the credentials or the message. Revocation is
/// checked online unless <see cref="AuthOptions.SmtpCheckRevocation"/> is off. Registered
/// only when every SMTP setting is present (<see cref="AuthOptions"/> rejects partial settings).
/// <paramref name="createClient"/> exists for the test suites, which register a client that
/// trusts only their relay's certificate, as they register TerraBase's <see cref="HttpClient"/>.
/// </summary>
public sealed partial class SmtpEmailSender(AuthOptions options, ILogger<SmtpEmailSender> logger, Func<SmtpClient>? createClient = null) : IEmailSender
{
    private const int TimeoutMilliseconds = 30_000;

    /// <inheritdoc />
    public async Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new MimeMessage();
            // Bare addresses only: Parse would turn "Name <other@example>" into a different recipient.
            message.From.Add(new MailboxAddress(string.Empty, options.SmtpFromAddress!));
            message.To.Add(new MailboxAddress(string.Empty, recipient));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var client = createClient?.Invoke() ?? new SmtpClient();
            client.Timeout = TimeoutMilliseconds;
            client.CheckCertificateRevocation = options.SmtpCheckRevocation;
            await client.ConnectAsync(options.SmtpHost!, options.SmtpPort, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(options.SmtpUsername!, options.SmtpPassword!, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Caller cancellation wins over whatever failure it raced with; every other failure
            // is a swallowed delivery failure.
            cancellationToken.ThrowIfCancellationRequested();

            // The safe logger keeps only the exception type and frames: MailKit messages can
            // quote server replies and addresses. Logging is best effort: a failing provider
            // must not turn a swallowed delivery failure into a failed request.
            try
            {
                LogDeliveryFailed(logger, exception);
            }
            catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
            {
                _ = logFailure;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "SMTP delivery failed")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception);
}
