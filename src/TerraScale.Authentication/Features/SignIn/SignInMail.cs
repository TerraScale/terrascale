using System.Threading.Channels;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using ZLogger;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary>
/// Delivers emailed codes (sign-in, password reset, sign-up), invitations (TS-598) and lockout notices after the response, so request timing never tells an account from a stranger. Codes and invitations
/// use one worker; notices use another bounded queue and worker, so refused attempts cannot crowd out recovery mail.
/// A slow provider holds at most two requests in flight however many requests arrive. Codes queued
/// when the host stops are drained best effort within its shutdown timeout. Admission refuses a full queue before
/// accepting an authentication start or persisting an invitation.
/// </summary>
public sealed class SignInMail(IEmailSender email, AuthEmails emails, EmailSettings settings, ProjectAuthSettings projects, PlatformDirectory platform, TimeProvider clock, ILogger<SignInMail> logger) : BackgroundService
{
    /// <summary>Queued and reserved deliveries allowed before new requests are refused.</summary>
    public const int Capacity = 256;

    /// <summary>How long a code waits for the email configuration before it goes out with the defaults.</summary>
    public static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(3);

    /// <summary>After a failed configuration read, codes go out with the defaults this long without trying again.</summary>
    public static readonly TimeSpan ConfigBackoff = TimeSpan.FromSeconds(30);

    // Shared by both workers. ponytail: per replica, each worker can pay one timeout per backoff.
    private long configRetryAt;

    private readonly Lock admission = new();
    private int occupied;
    private int reservations;
    private bool accepting = true;

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
        using var reservation = this.Reserve();
        reservation.EnqueueIssue(issue, locale);
    }

    /// <summary>
    /// Reserves bounded delivery capacity before any durable work. A full or stopping queue answers 503 regardless
    /// of account existence. Dispose an unused reservation to release it; enqueue consumes it exactly once.
    /// </summary>
    public Reservation Reserve()
    {
        lock (this.admission)
        {
            if (!this.accepting || this.occupied == Capacity)
            {
                throw new ApiException(StatusCodes.Status503ServiceUnavailable, "email_queue_unavailable", "Email delivery is busy; retry later.");
            }

            this.occupied++;
            this.reservations++;
            return new Reservation(this);
        }
    }

    /// <summary>
    /// Queues account lookup and a persisted notice claim on the separate notice worker, then sends the claimed lockout notice. A null result
    /// suppresses unknown accounts, duplicate episodes and expired lockouts without affecting the HTTP response.
    /// </summary>
    public void EnqueueLockout(Func<Task<AuthEmailValues?>> claim, string locale = AuthEmails.English)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!this.notices.Writer.TryWrite((async () => await claim().ConfigureAwait(false) is { } values
            ? new Outgoing(values.Email, AuthEmailKind.Lockout, values)
            : null, locale)))
        {
            logger.ZLogWarning($"Lockout notice dropped: the notice delivery queue is full or stopping");
        }
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Accept nothing new; the worker drains what is queued and then ends.
        lock (this.admission)
        {
            this.accepting = false;
            this.CompleteIfStopped();
        }

        this.notices.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(this.DeliverAsync(this.queue.Reader, notice: false), this.DeliverAsync(this.notices.Reader, notice: true));

    private async Task DeliverAsync(ChannelReader<(Func<Task<Outgoing?>> Prepare, string Locale)> reader, bool notice)
    {
        // Not stoppingToken: codes already issued are still delivered while the host shuts down.
        await foreach (var (prepare, locale) in reader.ReadAllAsync(CancellationToken.None))
        {
            if (!notice)
            {
                lock (this.admission)
                {
                    this.occupied--;
                }
            }

            try
            {
                if (await prepare().ConfigureAwait(false) is not { } outgoing)
                {
                    continue;
                }

                var message = await emails.RenderAsync(outgoing.Kind, locale, outgoing.Values, await this.ConfigAsync(outgoing.Values.Tenant, outgoing.Kind), cancellationToken: CancellationToken.None);
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
    private async Task<EmailConfig?> ConfigAsync(string tenant, AuthEmailKind kind)
    {
        // Never inherit platform copy or a customer-controlled sender identity into project authentication mail.
        var defaults = Tenancy.IsProject(tenant) && tenant != "platform" && kind is AuthEmailKind.SignInCode or AuthEmailKind.VerifyEmail or AuthEmailKind.PasswordReset or AuthEmailKind.Lockout
            ? EmailConfig.Defaults(tenant) with
            {
                Branding = new EmailBranding { SenderName = "TerraScale", FooterText = "Sent by TerraScale Authentication." },
            }
            : null;

        if (clock.GetUtcNow().UtcTicks < Volatile.Read(ref this.configRetryAt))
        {
            return defaults;
        }

        try
        {
            using var timeout = new CancellationTokenSource(ConfigTimeout, clock);
            if (defaults is null)
            {
                return await settings.GetAsync(OidcIssuer.InstanceId, timeout.Token);
            }

            var project = await projects.GetAsync(tenant, timeout.Token);
            var name = project.Name ?? (await platform.ProjectAsync(tenant, timeout.Token))?.Value.Name;
            return defaults with
            {
                Branding = defaults.Branding with
                {
                    BrandName = name,
                    LogoUrl = project.LogoUrl,
                    PrimaryColor = project.ThemeColor,
                    FooterText = project.EmailMessage is { } copy ? copy + "\n\n" + defaults.Branding.FooterText : defaults.Branding.FooterText,
                },
            };
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Volatile.Write(ref this.configRetryAt, (clock.GetUtcNow() + ConfigBackoff).UtcTicks);
            logger.ZLogWarning(failure, $"Email configuration unavailable; sending the sign-in code with the defaults");
            return defaults;
        }
    }

    private void FinishReservation(Func<Task<Outgoing?>>? prepare, string locale)
    {
        lock (this.admission)
        {
            this.reservations--;
            if (prepare is null)
            {
                this.occupied--;
            }
            else if (!this.queue.Writer.TryWrite((prepare, locale)))
            {
                // Reserved + queued work never exceeds Capacity, and completion waits for reservations.
                throw new InvalidOperationException("Reserved email delivery capacity was lost.");
            }

            this.CompleteIfStopped();
        }
    }

    private void CompleteIfStopped()
    {
        if (!this.accepting && this.reservations == 0)
        {
            this.queue.Writer.TryComplete();
        }
    }

    /// <summary>A single delivery slot held across issuance or an invitation transaction.</summary>
    public sealed class Reservation : IDisposable
    {
        private SignInMail? owner;

        internal Reservation(SignInMail owner) => this.owner = owner;

        /// <summary>Queues issuance for known and unknown accounts alike, preserving response parity.</summary>
        public void EnqueueIssue(Func<Task<IssuedPasscode?>> issue, string locale = AuthEmails.English)
        {
            ArgumentNullException.ThrowIfNull(issue);
            this.Consume().FinishReservation(async () => await issue().ConfigureAwait(false) is { } passcode
                ? new Outgoing(passcode.Email, passcode.Purpose switch
                {
                    PasscodePurpose.PasswordReset => AuthEmailKind.PasswordReset,
                    PasscodePurpose.SignUp => AuthEmailKind.VerifyEmail,
                    _ => AuthEmailKind.SignInCode,
                }, new AuthEmailValues(passcode.Email) { Code = passcode.Code, ExpiresIn = Passcodes.Lifetime, Tenant = passcode.Tenant })
                : null, locale);
        }

        /// <summary>Queues a committed invitation using the slot reserved before its transaction.</summary>
        public void EnqueueInvitation(AuthEmailValues values, string locale = AuthEmails.English)
        {
            ArgumentNullException.ThrowIfNull(values);
            this.Consume().FinishReservation(() => Task.FromResult<Outgoing?>(new Outgoing(values.Email, AuthEmailKind.Invitation, values)), locale);
        }

        /// <inheritdoc />
        public void Dispose() => Interlocked.Exchange(ref this.owner, null)?.FinishReservation(null, AuthEmails.English);

        private SignInMail Consume() => Interlocked.Exchange(ref this.owner, null)
            ?? throw new InvalidOperationException("The email reservation has already been consumed or released.");
    }

    // One email to render and send; Values never formats its code or link.
    private sealed record Outgoing(string To, AuthEmailKind Kind, AuthEmailValues Values);
}
