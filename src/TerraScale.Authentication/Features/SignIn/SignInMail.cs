using Dapper;
using System.Collections.Concurrent;
using System.Diagnostics;
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
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class SignInMail(IEmailSender email, AuthEmails emails, EmailSettings settings, ProjectAuthSettings projects, PlatformDirectory platform,
    AuthSchema schema, AuthData data, Passcodes passcodes, Users users, AuthOptions options, TimeProvider clock, ILogger<SignInMail> logger) : BackgroundService
{
    public const int Capacity = 256;

    public const int MaxAttempts = 5;

    public static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan ConfigBackoff = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    private const string Instance = OidcIssuer.InstanceId;
    public const int DeliveryConcurrency = 8;

    private const int Batch = 64;

    private long configRetryAt;

    private readonly ConcurrentDictionary<(string Tenant, bool Project), Lazy<Task<EmailConfig?>>> configReads = new();

    private readonly Lock admission = new();
    private readonly HashSet<string> local = new(StringComparer.Ordinal);
    private int occupied;
    private int reservations;
    private bool accepting = true;

    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly Channel<(Func<Task<Outgoing?>> Prepare, string Locale)> notices =
        Channel.CreateBounded<(Func<Task<Outgoing?>>, string)>(new BoundedChannelOptions(Capacity) { SingleReader = true });

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

    public void EnqueueLockout(Func<Task<AuthEmailValues?>> claim, string locale = AuthEmails.English)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!this.notices.Writer.TryWrite((async () => await claim() is { } values
            ? new Outgoing(AuthEmailKind.Lockout, values)
            : null, locale)))
        {
            logger.ZLogWarning($"Lockout notice dropped: the notice delivery queue is full or stopping");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (this.admission)
        {
            this.accepting = false;
        }

        this.wake.Writer.TryWrite(true);
        this.notices.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken);
        await Task.WhenAll(this.DeliverOutboxAsync(), this.DeliverNoticesAsync());
    }

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
        var active = new List<Task>(DeliveryConcurrency);
        while (true)
        {
            try
            {
                foreach (var completed in active.Where(task => task.IsCompleted).ToArray())
                {
                    active.Remove(completed);
                    await completed;
                }

                string[] pending;
                lock (this.admission)
                {
                    pending = [.. this.local];
                }
                if (pending.Length > 0)
                {
                    var open = (await data.ReadAsync(connection => connection.QueryAsync<string>(
                        "SELECT id FROM mail_outbox WHERE state IN ('queued', 'ready', 'sending') ORDER BY id LIMIT @Batch", new { Batch }), CancellationToken.None)).AsList();
                    if (open.Count < Batch)
                    {
                        this.Release(id => pending.Contains(id, StringComparer.Ordinal) && !open.Contains(id, StringComparer.Ordinal));
                    }
                }

                if (active.Count < DeliveryConcurrency)
                {
                    var now = clock.GetUtcNow();
                    var take = DeliveryConcurrency - active.Count;
                    var rows = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<MailRow>(
                        "SELECT * FROM mail_outbox WHERE state IN ('queued', 'ready', 'sending') AND lease_until <= @now ORDER BY lease_until LIMIT @take",
                        new { now, take }), CancellationToken.None));
                    foreach (var row in rows)
                    {
                        active.Add(this.ProcessAsync(row));
                    }
                }

                if (clock.GetUtcNow() >= pruneAt)
                {
                    await this.PruneAsync();
                    pruneAt = clock.GetUtcNow() + Lease;
                }
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                try
                {
                    logger.ZLogWarning(failure, $"Authentication email outbox pass failed; retrying");
                }
                catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
                {
                    _ = logFailure;
                }
            }

            if (this.Drained && active.Count == 0)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(PollInterval, clock);
            var awakened = this.wake.Reader.ReadAsync(timeout.Token).AsTask();
            await Task.WhenAny(active.Append(awakened));
            await timeout.CancelAsync();
            try
            {
                await awakened;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ProcessAsync(Versioned<MailRow> rowData)
    {
        var row = rowData.Value;
        this.Release(id => id == row.Id);
        if (row.LeaseUntil > clock.GetUtcNow() || await this.SaveAsync(row with { LeaseUntil = clock.GetUtcNow() + Lease }, rowData.Version) is not { } claimed)
        {
            return;
        }

        AuthMetrics.EmailStage(row.Attempts == 0 ? "queue" : "retry_wait", (clock.GetUtcNow() - row.CreatedAt).TotalSeconds);
        try
        {
            await this.AdvanceAsync(claimed.Row, claimed.Version);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            logger.ZLogError(failure, $"Authentication email delivery failed");
        }
    }

    private async Task AdvanceAsync(MailRow row, string version)
    {
        if (row.State == "sending")
        {
            await this.SaveAsync(row with { State = "unknown", Payload = null }, version);
            return;
        }

        var preparing = Stopwatch.GetTimestamp();
        var work = this.Open(row);
        if (row.State == "queued")
        {
            if (await this.IssueAsync(work) is not { } issued)
            {
                await data.SaveAsync(Change.Delete(row, version), CancellationToken.None);
                return;
            }

            work = new MailWork(issued.Tenant, issued.Email)
            {
                Kind = work.MagicBrowserHash is not null ? AuthEmailKind.MagicLink : issued.Purpose switch
                {
                    PasscodePurpose.PasswordReset => AuthEmailKind.PasswordReset,
                    PasscodePurpose.SignUp => AuthEmailKind.VerifyEmail,
                    PasscodePurpose.StepUp => AuthEmailKind.StepUpCode,
                    PasscodePurpose.Social => AuthEmailKind.SocialLinkCode,
                    _ => AuthEmailKind.SignInCode,
                },
                Values = new AuthEmailValues(issued.Email)
                {
                    Code = issued.Code,
                    Provider = work.Provider,
                    ExpiresIn = issued.ExpiresAt - clock.GetUtcNow(),
                    Tenant = issued.Tenant,
                    Link = work.MagicBrowserHash is { } browserHash ? MagicLinks.Create(options, issued, browserHash, work.MagicNext) : null,
                },
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
            && !await passcodes.IsCurrentAsync(row.Tenant!, row.Purpose!.Value, row.CodeKey!, row.IssueId, CancellationToken.None))
        {
            await this.SaveAsync(row with { State = "suppressed", Payload = null }, version);
            return;
        }

        var values = work.Values!;
        var message = await emails.RenderAsync(work.Kind!.Value, row.Locale, values, await this.ConfigAsync(values.Tenant, work.Kind.Value), cancellationToken: CancellationToken.None);
        if (await this.SaveAsync(row with { State = "sending" }, version) is not { } sending)
        {
            return;
        }

        AuthMetrics.EmailStage("prepare", Stopwatch.GetElapsedTime(preparing).TotalSeconds);
        var sendingAt = Stopwatch.GetTimestamp();
        EmailDelivery delivery;
        try
        {
            delivery = await email.DeliverUnlessPausedAsync(values.Email, message, CancellationToken.None);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            logger.ZLogError(failure, $"Authentication email delivery failed");
            delivery = new EmailSent(EmailDeliveryOutcome.Unknown);
        }
        finally
        {
            AuthMetrics.EmailStage("send", Stopwatch.GetElapsedTime(sendingAt).TotalSeconds);
        }

        var (outcome, pausedUntil) = Settled(delivery);
        if (pausedUntil is { } until)
        {
            await this.SaveAsync(row with { LeaseUntil = until }, sending.Version);
            return;
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
            }, sending.Version);
    }

    private static (EmailDeliveryOutcome Outcome, DateTimeOffset? PausedUntil) Settled(EmailDelivery delivery) => delivery switch
    {
        EmailSent sent => (sent.Outcome, null),
        EmailPaused paused => (EmailDeliveryOutcome.Retryable, paused.Until),
    };

    private async Task<IssuedPasscode?> IssueAsync(MailWork work) => work.SocialLink is { } pending
        ? await passcodes.RequestSocialAsync(work.Tenant, pending, CancellationToken.None)
        : work.MagicBrowserHash is not null
        ? await passcodes.RequestMagicLinkAsync(work.Tenant, work.Email, CancellationToken.None)
        : work.Purpose switch
        {
            PasscodePurpose.SignUp when work.Resend => await passcodes.ResendSignUpAsync(work.Tenant, work.Email, CancellationToken.None),
            PasscodePurpose.SignUp => await passcodes.RequestSignUpAsync(work.Tenant, work.Email, work.PasswordHash!, work.PendingToken!, work.Username, CancellationToken.None),
            PasscodePurpose.StepUp => await users.GetAsync(work.Tenant, work.UserId!, CancellationToken.None) is { } user
                ? await passcodes.RequestStepUpAsync(user, work.RequestId!, CancellationToken.None)
                : null,
            PasscodePurpose.EnrollmentInbox => await users.GetAsync(work.Tenant, work.UserId!, CancellationToken.None) is { } enrolling
                ? await passcodes.RequestEnrollmentInboxAsync(enrolling, work.RequestId!, CancellationToken.None)
                : null,
            { } purpose => await passcodes.RequestAsync(work.Tenant, work.Email, purpose, CancellationToken.None),
            null => throw new InvalidOperationException("A queued outbox row names no code to issue."),
        };

    private async Task PruneAsync()
    {
        var cutoff = clock.GetUtcNow() - Retention;
        var batchSize = SessionPruner.BatchSize;
        await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            """
            DELETE FROM mail_outbox WHERE id IN (SELECT id FROM mail_outbox WHERE created_at <= @cutoff
                AND (state IN ('sent', 'rejected', 'failed', 'suppressed', 'unknown') OR lease_until <= @cutoff) ORDER BY created_at LIMIT @batchSize)
            """, new { cutoff, batchSize }),
            CancellationToken.None);
    }

    private async Task<(MailRow Row, string Version)?> SaveAsync(MailRow row, string version)
    {
        var saved = Change.Update(row, version);
        return await data.SaveAsync(saved, CancellationToken.None) ? (row, saved.Version!) : null;
    }

    private string Seal(string id, MailWork work) =>
        SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(work, MailJson.Default.MailWork), "auth-mail:" + id);

    private MailWork Open(MailRow row) =>
        JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, row.Payload ?? throw new InvalidOperationException("An open outbox row has no payload."), "auth-mail:" + row.Id), MailJson.Default.MailWork)
            ?? throw new InvalidOperationException("An outbox payload is empty.");

    private async Task DeliverNoticesAsync()
    {
        await foreach (var (prepare, locale) in this.notices.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                if (await prepare() is not { } outgoing)
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
                logger.ZLogError(failure, $"Authentication email delivery failed");
            }
        }
    }

    private async Task<EmailConfig?> ConfigAsync(string tenant, AuthEmailKind kind)
    {
        var defaults = Tenancy.IsProject(tenant) && tenant != "platform" && kind is AuthEmailKind.SignInCode or AuthEmailKind.SocialLinkCode or AuthEmailKind.MagicLink or AuthEmailKind.VerifyEmail or AuthEmailKind.PasswordReset or AuthEmailKind.Lockout or AuthEmailKind.NewDevice
            ? EmailConfig.Defaults(tenant) with
            {
                Branding = new EmailBranding { SenderName = "TerraScale", FooterText = "Sent by TerraScale Authentication." },
            }
            : null;

        if (clock.GetUtcNow().UtcTicks < Volatile.Read(ref this.configRetryAt))
        {
            return defaults;
        }

        var key = (tenant, defaults is not null);
        var read = this.configReads.GetOrAdd(key, _ => new Lazy<Task<EmailConfig?>>(() => this.ReadConfigAsync(tenant, defaults)));
        try
        {
            return await read.Value;
        }
        finally
        {
            this.configReads.TryRemove(new KeyValuePair<(string, bool), Lazy<Task<EmailConfig?>>>(key, read));
        }
    }

    private async Task<EmailConfig?> ReadConfigAsync(string tenant, EmailConfig? defaults)
    {
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

    private MailRow Prepare(string state, MailWork work, string locale, TimeSpan? lifetime)
    {
        var now = clock.GetUtcNow();
        var id = AuthIds.New();
        return new MailRow(id, state, locale, now, now, this.Seal(id, work)) { ExpiresAt = now + lifetime };
    }

    private async Task WriteAsync(string state, MailWork work, string locale, TimeSpan? lifetime)
    {
        var row = this.Prepare(state, work, locale, lifetime);
        var id = row.Id;
        var written = false;
        try
        {
            written = await data.SaveAsync(Change.Insert(row), CancellationToken.None) ? true : throw ApiException.Unavailable();
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

    public sealed class Reservation : IDisposable
    {
        private SignInMail? owner;

        internal Reservation(SignInMail owner) => this.owner = owner;

        public Task EnqueueMagicLinkAsync(string tenant, string email, string browserHash, string? next, string locale) =>
            this.Consume().WriteAsync("queued", new MailWork(tenant, email) { Purpose = PasscodePurpose.SignIn, MagicBrowserHash = browserHash, MagicNext = next }, locale, MagicLinks.Lifetime);

        public Task EnqueueCodeAsync(string tenant, string email, PasscodePurpose purpose, string locale = AuthEmails.English) =>
            this.Consume().WriteAsync("queued", new MailWork(tenant, email) { Purpose = purpose }, locale, null);

        public Task EnqueueSocialAsync(string tenant, string email, string pending, string provider, string locale) =>
            this.Consume().WriteAsync("queued", new MailWork(tenant, email) { Purpose = PasscodePurpose.Social, SocialLink = pending, Provider = provider }, locale, null);

        public Task EnqueueSignUpAsync(string tenant, string email, string passwordHash, string pendingToken, string locale = AuthEmails.English, string? username = null) =>
            this.Consume().WriteAsync("queued", new MailWork(tenant, email) { Purpose = PasscodePurpose.SignUp, PasswordHash = passwordHash, PendingToken = pendingToken, Username = username }, locale, null);

        public Task EnqueueSignUpResendAsync(string tenant, string email, string locale = AuthEmails.English) =>
            this.Consume().WriteAsync("queued", new MailWork(tenant, email) { Purpose = PasscodePurpose.SignUp, Resend = true }, locale, null);

        public Task EnqueueStepUpAsync(UserRecord user, string requestId, string locale = AuthEmails.English)
        {
            ArgumentNullException.ThrowIfNull(user);
            return this.Consume().WriteAsync("queued", new MailWork(user.InstanceId, user.Email) { Purpose = PasscodePurpose.StepUp, UserId = user.Id, RequestId = requestId }, locale, null);
        }

        public Task EnqueueEnrollmentInboxAsync(UserRecord user, string challenge, string locale) =>
            this.Consume().WriteAsync("queued", new MailWork(user.InstanceId, user.Email)
            { Purpose = PasscodePurpose.EnrollmentInbox, UserId = user.Id, RequestId = challenge }, locale, null);

        public Task EnqueueEmailAsync(AuthEmailKind kind, AuthEmailValues values, string locale = AuthEmails.English)
        {
            ArgumentNullException.ThrowIfNull(values);
            return this.Consume().WriteAsync("ready", new MailWork(values.Tenant, values.Email) { Kind = kind, Values = values }, locale, values.ExpiresIn);
        }

        public MailRow Stage(AuthEmailKind kind, AuthEmailValues values, string locale = AuthEmails.English)
        {
            ArgumentNullException.ThrowIfNull(values);
            var mail = this.owner ?? throw new InvalidOperationException("The email reservation has already been consumed or released.");
            return mail.Prepare("ready", new MailWork(values.Tenant, values.Email) { Kind = kind, Values = values }, locale, values.ExpiresIn);
        }

        public void Committed(MailRow row)
        {
            ArgumentNullException.ThrowIfNull(row);
            this.Consume().FinishReservation(row.Id);
        }

        public void Dispose() => Interlocked.Exchange(ref this.owner, null)?.FinishReservation(null);

        private SignInMail Consume() => Interlocked.Exchange(ref this.owner, null)
            ?? throw new InvalidOperationException("The email reservation has already been consumed or released.");
    }

    private sealed record Outgoing(AuthEmailKind Kind, AuthEmailValues Values);
}

public sealed record MailRow(string Id, string State, string Locale, DateTimeOffset CreatedAt, DateTimeOffset LeaseUntil, string? Payload)
{
    public string? Version { get; init; }

    public int Attempts { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? Tenant { get; init; }

    public PasscodePurpose? Purpose { get; init; }

    public string? CodeKey { get; init; }

    public string? IssueId { get; init; }

    public override string ToString() => nameof(MailRow);
}

internal sealed record MailWork(string Tenant, string Email)
{
    public string? SocialLink { get; init; }

    public string? Provider { get; init; }

    public string? MagicBrowserHash { get; init; }

    public string? MagicNext { get; init; }

    public string? Username { get; init; }

    public PasscodePurpose? Purpose { get; init; }

    public string? PasswordHash { get; init; }

    public string? PendingToken { get; init; }

    public bool Resend { get; init; }

    public string? UserId { get; init; }

    public string? RequestId { get; init; }

    public AuthEmailKind? Kind { get; init; }

    public AuthEmailValues? Values { get; init; }

    public override string ToString() => nameof(MailWork);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MailWork))]
internal sealed partial class MailJson : JsonSerializerContext;
