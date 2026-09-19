namespace TerraScale.Auth.Email;

/// <summary>Outbound email abstraction. The default implementation logs; a deployment can wire SMTP or a provider.</summary>
public interface IEmailSender
{
    /// <summary>Sends a transactional email. Implementations must never throw.</summary>
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>Logs emails instead of sending them — the zero-config default (dev/test).</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        logger.LogInformation("email to {To}: {Subject}\n{Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
