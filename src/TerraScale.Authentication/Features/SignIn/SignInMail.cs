using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary>
/// Delivers emailed codes (sign-in, password reset, sign-up, step-up), invitations and email change mail after the response,
/// through a durable outbox in TerraBase, so request timing never tells an account from a stranger and a crash or restart
/// loses no accepted work. A request persists only what was asked, sealed under the master key; the worker issues the code,
/// binds the row to that issuance, and right before sending checks that the code still redeems, so an expired or superseded
/// code is never delivered. The row is marked sending before the provider call: a crash then, or an ambiguous provider
/// outcome, ends as unknown and is never resent; an explicit retryable refusal is retried with backoff. Rows a stopped
/// replica leased are taken over by any replica once the lease lapses, and finished rows keep no payload and are deleted
/// after <see cref="Retention"/>. Lockout notices stay a best-effort in-memory queue on their own worker, so refused
/// attempts cannot crowd out recovery mail. Admission refuses a full replica before accepting an authentication start or
/// persisting an invitation.
/// </summary>
public sealed class SignInMail(IEmailSender email, AuthEmails emails, EmailSettings settings, ProjectAuthSettings projects, PlatformDirectory platform,
    AuthStore store, Passcodes passcodes, Users users, AuthOptions options, TimeProvider clock, ILogger<SignInMail> logger) : BackgroundService
{
    /// <summary>Accepted but not yet claimed deliveries, plus reservations, allowed per replica before new requests are refused.</summary>
    public const int Capacity = 256;

    /// <summary>Provider refusals known not to have sent that one email survives before it is given up.</summary>
    public const int MaxAttempts = 5;

    /// <summary>How long the email configuration may take before a code goes out with the defaults.</summary>
    public static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(3);

    /// <summary>After a failed configuration read, codes go out with the defaults this long without trying again.</summary>
    public static readonly TimeSpan ConfigBackoff = TimeSpan.FromSeconds(30);

    /// <summary>How long a replica owns a claimed row; after it any replica takes the row over.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>How often the worker looks for rows another replica left behind or deferred.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>First retry delay after a retryable refusal; it doubles per attempt.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>How long finished rows, without their payload, are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    private const string Instance = OidcIssuer.InstanceId;
    private const int Batch = 64;
    private static readonly JsonElement OpenRows = JsonDocument.Parse("""{"or":[{"eq":["/state","queued"]},{"eq":["/state","ready"]},{"eq":["/state","sending"]}]}""").RootElement.Clone();

    // Shared by both workers. ponytail: per replica, each worker can pay one timeout per backoff.
    private long configRetryAt;

    private readonly Lock admission = new();
    private readonly HashSet<string> local = new(StringComparer.Ordinal);
    private int occupied;
    private int reservations;
    private bool accepting = true;

    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly Channel<(Func<Task<Outgoing?>> Prepare, string Locale)> notices =
        Channel.CreateBounded<(Func<Task<Outgoing?>>, string)>(new BoundedChannelOptions(Capacity) { SingleReader = true });

    /// <summary>
    /// Reserves bounded delivery capacity before any durable work. A full or stopping replica answers 503 regardless
    /// of account existence. Dispose an unused reservation to release it; an enqueue consumes it exactly once.
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
            ? new Outgoing(AuthEmailKind.Lockout, values)
            : null, locale)))
        {
            logger.ZLogWarning($"Lockout notice dropped: the notice delivery queue is full or stopping");
        }
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Accept nothing new; the worker delivers what this replica accepted and then ends. Anything left is recovered from the outbox.
        lock (this.admission)
        {
            this.accepting = false;
        }

        this.wake.Writer.TryWrite(true);
        this.notices.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(this.DeliverOutboxAsync(), this.DeliverNoticesAsync());

    private bool Drained
    {
        get
        {
            lock (this.admission)
            {
                return !this.accepting && this.reservations == 0 && this.occupied == 0;
            }
        }
    }

    private async Task DeliverOutboxAsync()
    {
        var pruneAt = DateTimeOffset.MinValue;
        // Not stoppingToken: accepted codes are still delivered while the host shuts down.
        while (true)
        {
            var drained = this.Drained;
            var full = false;
            try
            {
                var rows = await store.QueryAsync(AuthStore.MailOutboxCollection, OpenRows, Batch, CancellationToken.None).ConfigureAwait(false);
                full = rows.Count == Batch;
                foreach (var row in rows)
                {
                    await this.ProcessAsync(row).ConfigureAwait(false);
                }

                if (!full)
                {
                    // Rows this replica accepted that are no longer open were finished elsewhere.
                    this.Release(id => !rows.Any(row => row.Id == AuthStore.NaturalKeyId(Instance, AuthStore.MailOutboxCollection, id)));
                }

                if (clock.GetUtcNow() >= pruneAt)
                {
                    await this.PruneAsync().ConfigureAwait(false);
                    pruneAt = clock.GetUtcNow() + Lease;
                }
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                logger.ZLogError(failure, $"Authentication email outbox pass failed");
            }

            if (drained)
            {
                return;
            }

            if (!full)
            {
                using var timeout = new CancellationTokenSource(PollInterval, clock);
                try
                {
                    await this.wake.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Poll for rows other replicas left behind.
                }
            }
        }
    }

    private async Task ProcessAsync(TerraBaseDocument document)
    {
        var row = document.Document.Deserialize(MailJson.Default.MailRow);
        if (row is null)
        {
            return;
        }

        this.Release(id => id == row.Id);
        if (row.LeaseUntil > clock.GetUtcNow() || await this.SaveAsync(row with { LeaseUntil = clock.GetUtcNow() + Lease }, document.Version) is not { } claimed)
        {
            return;
        }

        try
        {
            await this.AdvanceAsync(claimed.Row, claimed.Version).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // The lease lapses and the row is retried; a row already marked sending ends as unknown.
            logger.ZLogError(failure, $"Authentication email delivery failed");
        }
    }

    private async Task AdvanceAsync(MailRow row, string version)
    {
        if (row.State == "sending")
        {
            // A replica stopped during the provider call, which may have sent: never send it again.
            await this.SaveAsync(row with { State = "unknown", Payload = null }, version).ConfigureAwait(false);
            return;
        }

        var work = this.Open(row);
        if (row.State == "queued")
        {
            if (await this.IssueAsync(work).ConfigureAwait(false) is not { } issued)
            {
                // No account, a reached issue limit or a lost race: nothing to send, nothing to keep.
                await store.CompareAndDeleteAsync(AuthStore.MailOutboxCollection, Instance, row.Id, version, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            work = new MailWork(issued.Tenant, issued.Email)
            {
                Kind = issued.Purpose switch
                {
                    PasscodePurpose.PasswordReset => AuthEmailKind.PasswordReset,
                    PasscodePurpose.SignUp => AuthEmailKind.VerifyEmail,
                    _ => AuthEmailKind.SignInCode,
                },
                Values = new AuthEmailValues(issued.Email) { Code = issued.Code, ExpiresIn = Passcodes.Lifetime, Tenant = issued.Tenant },
            };
            if (await this.SaveAsync(row with
            {
                State = "ready",
                Payload = this.Seal(row.Id, work),
                ExpiresAt = issued.ExpiresAt,
                Tenant = issued.Tenant,
                Purpose = issued.Purpose,
                CodeKey = issued.Key,
                IssueId = issued.IssueId,
            }, version) is not { } ready)
            {
                return;
            }

            (row, version) = ready;
        }

        if (row.ExpiresAt <= clock.GetUtcNow() || row.IssueId is not null
            && !await passcodes.IsCurrentAsync(row.Tenant!, row.Purpose!.Value, row.CodeKey!, row.IssueId, CancellationToken.None).ConfigureAwait(false))
        {
            await this.SaveAsync(row with { State = "suppressed", Payload = null }, version).ConfigureAwait(false);
            return;
        }

        var values = work.Values!;
        var message = await emails.RenderAsync(work.Kind!.Value, row.Locale, values, await this.ConfigAsync(values.Tenant, work.Kind.Value), cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (await this.SaveAsync(row with { State = "sending" }, version) is not { } sending)
        {
            return;
        }

        EmailDeliveryOutcome outcome;
        try
        {
            outcome = await email.DeliverAsync(values.Email, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            logger.ZLogError(failure, $"Authentication email delivery failed");
            outcome = EmailDeliveryOutcome.Unknown;
        }

        await this.SaveAsync(outcome == EmailDeliveryOutcome.Retryable && row.Attempts + 1 < MaxAttempts
            ? row with { Attempts = row.Attempts + 1, LeaseUntil = clock.GetUtcNow() + RetryDelay * (1 << row.Attempts) }
            : row with
            {
                State = outcome switch
                {
                    EmailDeliveryOutcome.Accepted => "sent",
                    EmailDeliveryOutcome.Rejected => "rejected",
                    EmailDeliveryOutcome.Retryable => "failed",
                    _ => "unknown",
                },
                Payload = null,
            }, sending.Version).ConfigureAwait(false);
    }

    private async Task<IssuedPasscode?> IssueAsync(MailWork work) => work.Purpose switch
    {
        PasscodePurpose.SignUp => await passcodes.RequestSignUpAsync(work.Tenant, work.Email, work.PasswordHash!, work.PendingToken!, CancellationToken.None).ConfigureAwait(false),
        PasscodePurpose.StepUp => await users.GetAsync(work.Tenant, work.UserId!, CancellationToken.None).ConfigureAwait(false) is { } user
            ? await passcodes.RequestStepUpAsync(user, work.RequestId!, CancellationToken.None).ConfigureAwait(false)
            : null,
        { } purpose => await passcodes.RequestAsync(work.Tenant, work.Email, purpose, CancellationToken.None).ConfigureAwait(false),
        null => throw new InvalidOperationException("A queued outbox row names no code to issue."),
    };

    private async Task PruneAsync()
    {
        var cutoff = JsonSerializer.Serialize(clock.GetUtcNow() - Retention, MailJson.Default.DateTimeOffset);
        using var filter = JsonDocument.Parse($$"""{"lte":["/createdAt",{{cutoff}}]}""");
        foreach (var document in await store.QueryAsync(AuthStore.MailOutboxCollection, filter.RootElement, Batch, CancellationToken.None).ConfigureAwait(false))
        {
            if (document.Document.Deserialize(MailJson.Default.MailRow) is { } row)
            {
                await store.CompareAndDeleteAsync(AuthStore.MailOutboxCollection, Instance, row.Id, document.Version, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<(MailRow Row, string Version)?> SaveAsync(MailRow row, string version) =>
        await store.ReplaceAsync(AuthStore.MailOutboxCollection, Instance, row.Id, JsonSerializer.SerializeToElement(row, MailJson.Default.MailRow), version, CancellationToken.None).ConfigureAwait(false) is { } saved
            ? (row, saved)
            : null;

    private string Seal(string id, MailWork work) =>
        SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(work, MailJson.Default.MailWork), "auth-mail:" + id);

    private MailWork Open(MailRow row) =>
        JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, row.Payload ?? throw new InvalidOperationException("An open outbox row has no payload."), "auth-mail:" + row.Id), MailJson.Default.MailWork)
            ?? throw new InvalidOperationException("An outbox payload is empty.");

    private async Task DeliverNoticesAsync()
    {
        // Not stoppingToken: notices already claimed are still delivered while the host shuts down.
        await foreach (var (prepare, locale) in this.notices.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                if (await prepare().ConfigureAwait(false) is not { } outgoing)
                {
                    continue;
                }

                var message = await emails.RenderAsync(outgoing.Kind, locale, outgoing.Values, await this.ConfigAsync(outgoing.Values.Tenant, outgoing.Kind), cancellationToken: CancellationToken.None);
                if (outgoing.Values.LockoutUntil <= clock.GetUtcNow())
                {
                    continue;
                }

                await email.SendAsync(outgoing.Values.Email, message, CancellationToken.None);
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

    // Persists one accepted email before the request answers; the same write for every address, account or not.
    private async Task WriteAsync(string state, MailWork work, string locale, TimeSpan? lifetime)
    {
        var now = clock.GetUtcNow();
        var id = AuthStore.NewId();
        var row = new MailRow(id, state, locale, now, now, this.Seal(id, work)) { ExpiresAt = now + lifetime };
        var written = false;
        try
        {
            await store.CreateAsync(AuthStore.MailOutboxCollection, Instance, id, JsonSerializer.SerializeToElement(row, MailJson.Default.MailRow), CancellationToken.None).ConfigureAwait(false);
            written = true;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            logger.ZLogError(failure, $"Authentication email could not be queued");
            throw ApiException.Unavailable();
        }
        finally
        {
            this.FinishReservation(written ? id : null);
        }
    }

    private void FinishReservation(string? id)
    {
        lock (this.admission)
        {
            this.reservations--;
            if (id is null)
            {
                this.occupied--;
            }
            else
            {
                this.local.Add(id);
            }
        }

        this.wake.Writer.TryWrite(true);
    }

    private void Release(Func<string, bool> finished)
    {
        lock (this.admission)
        {
            this.occupied -= this.local.RemoveWhere(id => finished(id));
        }
    }

    /// <summary>A single delivery slot held across issuance or an invitation transaction.</summary>
    public sealed class Reservation : IDisposable
    {
        private SignInMail? owner;

        internal Reservation(SignInMail owner) => this.owner = owner;

        /// <summary>Queues a sign-in or password-reset code; the worker issues it only if the address has an account.</summary>
        public Task EnqueueCodeAsync(string tenant, string email, PasscodePurpose purpose, string locale = AuthEmails.English) =>
            this.Consume().WriteAsync("queued", new MailWork(tenant, email) { Purpose = purpose }, locale, null);

        /// <summary>Queues a sign-up code, or a sign-in code when the address already has an account.</summary>
        public Task EnqueueSignUpAsync(string tenant, string email, string passwordHash, string pendingToken, string locale = AuthEmails.English) =>
            this.Consume().WriteAsync("queued", new MailWork(tenant, email) { Purpose = PasscodePurpose.SignUp, PasswordHash = passwordHash, PendingToken = pendingToken }, locale, null);

        /// <summary>Queues the step-up code of hosted request <paramref name="requestId"/>.</summary>
        public Task EnqueueStepUpAsync(UserRecord user, string requestId)
        {
            ArgumentNullException.ThrowIfNull(user);
            return this.Consume().WriteAsync("queued", new MailWork(user.InstanceId, user.Email) { Purpose = PasscodePurpose.StepUp, UserId = user.Id, RequestId = requestId }, AuthEmails.English, null);
        }

        /// <summary>Queues a committed <paramref name="kind"/> email using the slot reserved before its transaction.</summary>
        public Task EnqueueEmailAsync(AuthEmailKind kind, AuthEmailValues values, string locale = AuthEmails.English)
        {
            ArgumentNullException.ThrowIfNull(values);
            return this.Consume().WriteAsync("ready", new MailWork(values.Tenant, values.Email) { Kind = kind, Values = values }, locale, values.ExpiresIn);
        }

        /// <inheritdoc />
        public void Dispose() => Interlocked.Exchange(ref this.owner, null)?.FinishReservation(null);

        private SignInMail Consume() => Interlocked.Exchange(ref this.owner, null)
            ?? throw new InvalidOperationException("The email reservation has already been consumed or released.");
    }

    // One lockout notice to render and send; Values never formats its code or link.
    private sealed record Outgoing(AuthEmailKind Kind, AuthEmailValues Values);
}

/// <summary>One outbox row; <see cref="Payload"/> is the sealed <see cref="MailWork"/> and is dropped once the row finishes.</summary>
internal sealed record MailRow(string Id, string State, string Locale, DateTimeOffset CreatedAt, DateTimeOffset LeaseUntil, string? Payload)
{
    public int Attempts { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    // The issued code the row delivers; it is sent only while that code still redeems.
    public string? Tenant { get; init; }

    public PasscodePurpose? Purpose { get; init; }

    public string? CodeKey { get; init; }

    public string? IssueId { get; init; }

    public override string ToString() => nameof(MailRow);
}

/// <summary>A code to issue (<see cref="Purpose"/>) or an email to send (<see cref="Kind"/>).</summary>
internal sealed record MailWork(string Tenant, string Email)
{
    public PasscodePurpose? Purpose { get; init; }

    public string? PasswordHash { get; init; }

    public string? PendingToken { get; init; }

    public string? UserId { get; init; }

    public string? RequestId { get; init; }

    public AuthEmailKind? Kind { get; init; }

    public AuthEmailValues? Values { get; init; }

    public override string ToString() => nameof(MailWork);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MailRow))]
[JsonSerializable(typeof(MailWork))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class MailJson : JsonSerializerContext;
