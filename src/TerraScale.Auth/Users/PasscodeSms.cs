namespace TerraScale.Auth.Users;

/// <summary>
/// Outbound SMS abstraction for phone-channel passcodes, mirroring
/// <see cref="TerraScale.Auth.Email.IEmailSender"/>. The email passcode flow uses
/// <see cref="TerraScale.Auth.Email.IEmailSender"/>; this is the seam a future
/// SMS factor plugs into.
/// </summary>
public interface ISmsSender
{
    /// <summary>Sends a transactional text message. Implementations must never throw.</summary>
    Task SendAsync(string phone, string body, CancellationToken ct = default);
}

/// <summary>Logs texts instead of sending them — the zero-config default (dev/test).</summary>
public sealed class LoggingSmsSender(ILogger<LoggingSmsSender> logger) : ISmsSender
{
    public Task SendAsync(string phone, string body, CancellationToken ct = default)
    {
        logger.LogInformation("sms to {Phone}: {Body}", phone, body);
        return Task.CompletedTask;
    }
}
