using System.Threading.Channels;
using TerraScale.Auth.Features.Email;
using TerraScale.Auth.Features.Identity;

namespace TerraScale.Auth.Features.SignIn;

/// <summary>
/// Delivers sign-in codes after the response, so request timing never tells an account from a stranger. One worker
/// sends them in order, so a slow relay holds at most one SMTP connection however many requests arrive. Codes queued
/// when the host stops are still sent within its shutdown timeout. A full queue drops the code and logs it; the user
/// can ask for another.
/// </summary>
public sealed partial class SignInMail(IEmailSender email, ILogger<SignInMail> logger) : BackgroundService
{
    /// <summary>Codes waiting for delivery before new ones are dropped.</summary>
    public const int Capacity = 256;

    // ponytail: one sequential sender, add workers if a healthy relay cannot keep up with sign-in volume.
    private readonly Channel<IssuedPasscode> queue = Channel.CreateBounded<IssuedPasscode>(new BoundedChannelOptions(Capacity) { SingleReader = true });

    /// <summary>Queues <paramref name="passcode"/> for delivery without waiting for it.</summary>
    public void Enqueue(IssuedPasscode passcode)
    {
        ArgumentNullException.ThrowIfNull(passcode);
        if (!this.queue.Writer.TryWrite(passcode))
        {
            LogDropped(logger);
        }
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Accept nothing new; the worker drains what is queued and then ends.
        this.queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not stoppingToken: codes already issued are still delivered while the host shuts down.
        await foreach (var passcode in this.queue.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                await email.SendAsync(passcode.Email, "Your TerraScale sign-in code",
                    $"Your TerraScale sign-in code is {passcode.Code}. It expires in 10 minutes.\n\nIf you did not try to sign in, ignore this email.",
                    CancellationToken.None);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Senders log and swallow their own failures; anything else must not stop delivery of the rest.
                LogFailed(logger, failure);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in code dropped: the delivery queue is full")]
    private static partial void LogDropped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sign-in code delivery failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
