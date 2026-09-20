namespace TerraScale.Auth.Email;

using System.Net;
using System.Net.Mail;

/// <summary>Sends email over SMTP (OCI Email Delivery). Wired instead of <see cref="LoggingEmailSender"/> when TS_AUTH_SMTP_HOST is set.</summary>
public sealed class SmtpEmailSender(
    string host, int port, string username, string password, string fromAddress, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        try
        {
            using var message = new MailMessage(fromAddress, to, subject, body);
            // SmtpClient is not concurrency-safe and ts-auth sends are low-volume:
            // one client (one connection) per email.
            using var client = new SmtpClient(host, port)
            {
                EnableSsl = true, // STARTTLS on 587/2525
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(username, password),
            };
            // Bound the whole send: a black-holed SMTP host (dropped SYNs) would
            // otherwise hold the calling HTTP request for the OS TCP connect timeout.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await client.SendMailAsync(message, timeout.Token);
        }
        catch (Exception exception)
        {
            // IEmailSender must never throw — a failed email must not fail the flow around it.
            logger.LogError(exception, "SMTP send to {To} failed (subject: {Subject})", to, subject);
        }
    }
}
