using System.Threading.Channels;
using TerraScale.Auth.Features.Email;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;
using ZLogger;

namespace TerraScale.Auth.Features.SignIn;

/// <summary>
/// Delivers emailed codes (sign-in, password reset, sign-up), invitations (TS-598) and lockout notices after the response, so request timing never tells an account from a stranger. Codes and invitations
/// use one worker; notices use another bounded queue and worker, so refused attempts cannot crowd out recovery mail.
/// A slow relay holds at most two SMTP connections however many requests arrive. Codes queued
/// when the host stops are still sent within its shutdown timeout. A full queue drops the code and logs it; the user
/// can ask for another.
/// </summary>
public sealed class SignInMail(IEmailSender email, AuthEmails emails, EmailSettings settings, TimeProvider clock, ILogger<SignInMail> logger) : BackgroundService
{
    /// <summary>Codes waiting for delivery before new ones are dropped.</summary>
    public const int Capacity = 256;

    /// <summary>How long a code waits for the email configuration before it goes out with the defaults.</summary>
    public static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(3);

    /// <summary>After a failed configuration read, codes go out with the defaults this long without trying again.</summary>
    public static readonly TimeSpan ConfigBackoff = TimeSpan.FromSeconds(30);

    // Shared by both workers. ponytail: per replica, each worker can pay one timeout per backoff.
    private long configRetryAt;

    // ponytail: one sequential sender per queue, add workers if a healthy relay cannot keep up with sign-in volume.
    private readonly Channel<(Func<Task<Outgoing?>> Prepare, string Locale)> queue =
        Channel.CreateBounded<(Func<Task<Outgoing?>>, string)>(new BoundedChannelOptions(Capacity) { SingleReader = true });

    private readonly Channel<(Func<Task<Outgoing?>> Prepare, string Locale)> notices =
        Channel.CreateBounded<(Func<Task<Outgoing?>>, string)>(new BoundedChannelOptions(Capacity) { SingleReader = true });

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
        this.Enqueue(async () => await issue().ConfigureAwait(false) is { } passcode
            ? new Outgoing(passcode.Email, passcode.Purpose switch
            {
                PasscodePurpose.PasswordReset => AuthEmailKind.PasswordReset,
                PasscodePurpose.SignUp => AuthEmailKind.VerifyEmail,
                _ => AuthEmailKind.SignInCode,
            }, new AuthEmailValues(passcode.Email) { Code = passcode.Code, ExpiresIn = Passcodes.Lifetime })
            : null, locale);
    }

    /// <summary>Queues an invitation to <paramref name="values"/>'s address, with its accept link, for delivery.</summary>
    public void EnqueueInvitation(AuthEmailValues values, string locale = AuthEmails.English)
    {
        ArgumentNullException.ThrowIfNull(values);
        this.Enqueue(() => Task.FromResult<Outgoing?>(new Outgoing(values.Email, AuthEmailKind.Invitation, values)), locale);
    }

    /// <summary>
    /// Queues account lookup and a persisted notice claim on the separate notice worker, then sends the claimed lockout notice. A null result
    /// suppresses unknown accounts, duplicate episodes and expired lockouts without affecting the HTTP response.
    /// </summary>
    public void EnqueueLockout(Func<Task<AuthEmailValues?>> claim, string locale = AuthEmails.English)
    {
        ArgumentNullException.ThrowIfNull(claim);
        this.Enqueue(async () => await claim().ConfigureAwait(false) is { } values
            ? new Outgoing(values.Email, AuthEmailKind.Lockout, values)
            : null, locale, notice: true);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Accept nothing new; the worker drains what is queued and then ends.
        this.queue.Writer.TryComplete();
        this.notices.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(this.DeliverAsync(this.queue.Reader), this.DeliverAsync(this.notices.Reader));

    private async Task DeliverAsync(ChannelReader<(Func<Task<Outgoing?>> Prepare, string Locale)> reader)
    {
        // Not stoppingToken: codes already issued are still delivered while the host shuts down.
        await foreach (var (prepare, locale) in reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                if (await prepare().ConfigureAwait(false) is not { } outgoing)
                {
                    continue;
                }

                var message = await emails.RenderAsync(outgoing.Kind, locale, outgoing.Values, await this.ConfigAsync(), cancellationToken: CancellationToken.None);
                if (outgoing.Kind == AuthEmailKind.Lockout && outgoing.Values.LockoutUntil <= clock.GetUtcNow())
                {
                    continue;
                }

                await email.SendAsync(outgoing.To, message, CancellationToken.None);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Senders log and swallow their own failures; anything else must not stop delivery of the rest.
                logger.ZLogError(failure, $"Authentication email delivery failed");
            }
        }
    }

    /// <summary>
    /// The instance's email configuration, read for every code so a change applies to the next email on every replica.
    /// When it cannot be read the code still goes out, with the default branding and copy: delivering it matters more.
    /// Each queue has one worker, so a read waits at most <see cref="ConfigTimeout"/>, and after a failure the next
    /// <see cref="ConfigBackoff"/> of emails skip the read: a stalled store costs at most one timeout per worker, not one per email.
    /// </summary>
    private async Task<EmailConfig?> ConfigAsync()
    {
        if (clock.GetUtcNow().UtcTicks < Volatile.Read(ref this.configRetryAt))
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
            Volatile.Write(ref this.configRetryAt, (clock.GetUtcNow() + ConfigBackoff).UtcTicks);
            logger.ZLogWarning(failure, $"Email configuration unavailable; sending the sign-in code with the defaults");
            return null;
        }
    }

    private void Enqueue(Func<Task<Outgoing?>> prepare, string locale, bool notice = false)
    {
        if (!(notice ? this.notices : this.queue).Writer.TryWrite((prepare, locale)))
        {
            logger.ZLogWarning($"Authentication email dropped: the delivery queue is full");
        }
    }

    // One email to render and send; Values never formats its code or link.
    private sealed record Outgoing(string To, AuthEmailKind Kind, AuthEmailValues Values);
}
