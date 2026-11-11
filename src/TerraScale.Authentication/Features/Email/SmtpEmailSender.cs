using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Hosting;
using ZLogger;

namespace TerraScale.Authentication.Features.Email;

public sealed class SmtpEmailSender(SmtpEmailOptions email, ILogger<SmtpEmailSender> logger) : IEmailSender, IEmailProvider
{
    private const string SenderAddress = "no-reply@terrascale.tech";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) =>
        _ = await this.DeliverAsync(recipient, message, cancellationToken).ConfigureAwait(false);

    public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        => (await this.DeliverResultAsync(recipient, message, cancellationToken).ConfigureAwait(false)).Outcome;

    public async Task<EmailProviderResult> DeliverResultAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        var outcome = "error";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            using var client = new SmtpClient();
            client.CheckCertificateRevocation = false;
            client.Timeout = (int)ConnectTimeout.TotalMilliseconds;
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(email.Host, email.Port, timeout.Token).ConfigureAwait(false);
            await client.ConnectAsync(socket, email.Host, email.Port, email.RequireTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, timeout.Token).ConfigureAwait(false);
            try
            {
                await client.AuthenticateAsync(email.Username, email.Password, timeout.Token).ConfigureAwait(false);
                await client.SendAsync(new SendRequest(Format(recipient, message)), timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
            }

            AuthMetrics.Email("sent");
            outcome = "queued";
            return EmailDeliveryOutcome.Accepted;
        }
        catch (SmtpCommandException command) when ((int)command.StatusCode >= 400 && (int)command.StatusCode < 500)
        {
            AuthMetrics.Email("failed");
            this.LogSmtpFailure((int)command.StatusCode);
            return EmailDeliveryOutcome.Retryable;
        }
        catch (SmtpCommandException command) when ((int)command.StatusCode >= 500 && (int)command.StatusCode < 600)
        {
            AuthMetrics.Email("failed");
            outcome = "bounced";
            this.LogSmtpFailure((int)command.StatusCode);
            return EmailDeliveryOutcome.Rejected;
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

    private void LogSmtpFailure(int status)
    {
        try
        {
            logger.ZLogError($"Email delivery failed: SMTP answered {status:@Status}");
        }
        catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
        {
            _ = logFailure;
        }
    }

    private static MimeMessage Format(string recipient, EmailMessage message)
    {
        var mail = new MimeMessage
        {
            Subject = message.Subject,
            Body = message.Html is null
                ? new TextPart("plain") { Text = message.Text }
                : new Multipart("alternative") { new TextPart("plain") { Text = message.Text }, new TextPart("html") { Text = message.Html } },
        };
        mail.From.Add(new MailboxAddress(message.FromName, SenderAddress));
        mail.To.Add(MailboxAddress.Parse(recipient));
        if (message.ReplyTo is { } replyTo)
        {
            mail.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        }

        return mail;
    }
}
