using System.Threading.Channels;
using TerraScale.Auth.Features.Email;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;

namespace TerraScale.Auth.Features.SignIn;

/// <summary>
/// Delivers emailed codes (sign-in, password reset, sign-up) after the response, so request timing never tells an account from a stranger. One worker
/// sends them in order, so a slow relay holds at most one SMTP connection however many requests arrive. Codes queued
/// when the host stops are still sent within its shutdown timeout. A full queue drops the code and logs it; the user
/// can ask for another.
/// </summary>
public sealed partial class SignInMail(IEmailSender email, AuthEmails emails, EmailSettings settings, TimeProvider clock, ILogger<SignInMail> logger) : BackgroundService
{
    /// <summary>Codes waiting for delivery before new ones are dropped.</summary>
    public const int Capacity = 256;

    /// <summary>How long a code waits for the email configuration before it goes out with the defaults.</summary>
    public static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(3);

    /// <summary>After a failed configuration read, codes go out with the defaults this long without trying again.</summary>
    public static readonly TimeSpan ConfigBackoff = TimeSpan.FromSeconds(30);

    // Only the single worker reads and writes it. ponytail: per replica, so each replica pays one timeout per backoff.
    private DateTimeOffset configRetryAt;

    // ponytail: one sequential sender, add workers if a healthy relay cannot keep up with sign-in volume.
    private readonly Channel<(Func<Task<IssuedPasscode?>> Issue, string Locale)> queue =
        Channel.CreateBounded<(Func<Task<IssuedPasscode?>>, string)>(new BoundedChannelOptions(Capacity) { SingleReader = true });

    /// <summary>Queues <paramref name="passcode"/> for delivery in <paramref name="locale"/> without waiting for it.</summary>
    public void Enqueue(IssuedPasscode passcode, string locale = AuthEmails.English)
    {
        ArgumentNullException.ThrowIfNull(passcode);
        this.EnqueueIssue(() => Task.FromResult<IssuedPasscode?>(passcode), locale);
    }

    /// <summary>
    /// Queues <paramref name="issue"/> to run on the worker and mails the code it returns, if any. The request then does
    /// the same work whether or not the address has an account; issuing failures are logged, not answered.
    /// </summary>
    public void EnqueueIssue(Func<Task<IssuedPasscode?>> issue, string locale = AuthEmails.English)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (!this.queue.Writer.TryWrite((issue, locale)))
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
        await foreach (var (issue, locale) in this.queue.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                if (await issue().ConfigureAwait(false) is not { } passcode)
                {
                    continue;
                }

                var kind = passcode.Purpose switch
                {
                    PasscodePurpose.PasswordReset => AuthEmailKind.PasswordReset,
                    PasscodePurpose.SignUp => AuthEmailKind.VerifyEmail,
                    _ => AuthEmailKind.SignInCode,
                };
                var message = await emails.RenderAsync(kind, locale,
                    new AuthEmailValues(passcode.Email) { Code = passcode.Code, ExpiresIn = Passcodes.Lifetime }, await this.ConfigAsync(), cancellationToken: CancellationToken.None);
                await email.SendAsync(passcode.Email, message, CancellationToken.None);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Senders log and swallow their own failures; anything else must not stop delivery of the rest.
                LogFailed(logger, failure);
            }
        }
    }

    /// <summary>
    /// The instance's email configuration, read for every code so a change applies to the next email on every replica.
    /// When it cannot be read the code still goes out, with the default branding and copy: delivering it matters more.
    /// One worker sends every code, so a read waits at most <see cref="ConfigTimeout"/>, and after a failure the next
    /// <see cref="ConfigBackoff"/> of codes skip the read: a stalled store costs a backlog one timeout, not one per code.
    /// </summary>
    private async Task<EmailConfig?> ConfigAsync()
    {
        if (clock.GetUtcNow() < this.configRetryAt)
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(ConfigTimeout, clock);
            return await settings.GetAsync(OidcIssuer.InstanceId, timeout.Token);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            this.configRetryAt = clock.GetUtcNow() + ConfigBackoff;
            LogConfigUnavailable(logger, failure);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email configuration unavailable; sending the sign-in code with the defaults")]
    private static partial void LogConfigUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in code dropped: the delivery queue is full")]
    private static partial void LogDropped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sign-in code delivery failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
