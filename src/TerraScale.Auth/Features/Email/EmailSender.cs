using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using TerraScale.Auth.Configuration;
using ZLogger;

namespace TerraScale.Auth.Features.Email;

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
/// Used when no SMTP relay is configured: nothing is delivered, and the warning records only
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

        // Best effort, like the SMTP sender's failure log: a failing provider must not fail the flow.
        try
        {
            logger.ZLogWarning($"Email not delivered: no SMTP relay is configured (recipient {recipient.Length:@RecipientLength}, subject {message.Subject.Length:@SubjectLength}, text {message.Text.Length:@TextLength}, HTML {(message.Html?.Length ?? 0):@HtmlLength} characters)");
        }
        catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
        {
            _ = logFailure;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Authenticated SMTP (<c>TS_AUTH_SMTP_*</c>) with mandatory STARTTLS and certificate validation:
/// a relay that does not offer TLS never receives the credentials or the message. Revocation is
/// checked online unless <see cref="AuthOptions.SmtpCheckRevocation"/> is off. Registered
/// only when every SMTP setting is present (<see cref="AuthOptions"/> rejects partial settings).
/// <paramref name="createClient"/> exists for the test suites, which register a client that
/// trusts only their relay's certificate, as they register TerraBase's <see cref="HttpClient"/>.
/// </summary>
public sealed class SmtpEmailSender(AuthOptions options, ILogger<SmtpEmailSender> logger, Func<SmtpClient>? createClient = null) : IEmailSender
{
    private const int TimeoutMilliseconds = 30_000;

    /// <inheritdoc />
    public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var mime = new MimeMessage();
            // Bare addresses only: Parse would turn "Name <other@example>" into a different recipient.
            mime.From.Add(new MailboxAddress(message.FromName ?? string.Empty, options.SmtpFromAddress!));
            mime.To.Add(new MailboxAddress(string.Empty, recipient));
            if (message.ReplyTo is { } replyTo)
            {
                mime.ReplyTo.Add(new MailboxAddress(string.Empty, replyTo));
            }

            mime.Subject = message.Subject;
            // multipart/alternative with the text part first, when there is HTML.
            mime.Body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html }.ToMessageBody();

            using var client = createClient?.Invoke() ?? new SmtpClient();
            client.Timeout = TimeoutMilliseconds;
            client.CheckCertificateRevocation = options.SmtpCheckRevocation;
            await client.ConnectAsync(options.SmtpHost!, options.SmtpPort, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(options.SmtpUsername!, options.SmtpPassword!, cancellationToken);
            await client.SendAsync(mime, cancellationToken);
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
                logger.ZLogError(exception, $"SMTP delivery failed");
            }
            catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
            {
                _ = logFailure;
            }
        }
    }
}
