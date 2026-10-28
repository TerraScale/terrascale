using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Store;
using ZLogger;

namespace TerraScale.Authentication.Features.RateLimiting;

/// <summary>A refused attempt's persisted deadline and remaining delay, rounded up to whole seconds.</summary>
/// <param name="Until">The stored window's end, shared by all replicas even when their clocks differ.</param>
/// <param name="RetryAfter">How long this replica must wait before retrying.</param>
public readonly record struct AttemptRejection(DateTimeOffset Until, TimeSpan RetryAfter);

/// <summary>
/// Fixed attempt windows (one minute unless a caller chooses another) and elapsed cooldowns shared by every replica through conditional writes to
/// TerraBase, for authentication, recovery, verification and token endpoints. Each region conditionally updates only its
/// own bucket on its region's single TerraBase node; reads sum the three replicated components.
/// Delayed replication can admit extra attempts, but never replaces another region's contribution. A replica with a
/// slower clock never resets a window another replica has already advanced. When storage fails, the same budgets run
/// on this replica's in-memory buckets (<see cref="LocalAttemptStore"/>) instead, so brute-force protection holds per
/// replica and an outage of the limiter's storage never takes sign-in down by itself; every fallback is logged and
/// counted. Other storage faults (rejected credentials, damaged buckets), persistent contention, or a full in-memory
/// store fail closed with 503; an attempt is never admitted unrecorded.
/// </summary>
public sealed class AttemptLimiter(IDocumentStore storage, TimeProvider clock, AuthOptions options, string region = "gru", ILogger<AttemptLimiter>? logger = null)
{
    /// <summary>The TerraBase collection holding up to three regional <see cref="AttemptBucket"/> documents per limited subject, plus notice claims.</summary>
    public const string Collection = "rate_limits";

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    // More rounds than any realistic number of replicas racing on one subject, so only storage
    // that keeps rejecting writes (or a stampede beyond it) reaches the ceiling and fails closed.
    private const int MaxRounds = 64;

    // Milliseconds a token bucket's stored write time may lead this replica's clock before it counts as corrupt.
    private const long MaxClockSkew = 24 * 60 * 60 * 1000;

    private static readonly string[] Regions = ["gru", "sjc", "fra"];

    private readonly int regionIndex = Array.IndexOf(Regions, region) is var index && index >= 0
        ? index : throw new ArgumentException("The auth region must be gru, sjc or fra.", nameof(region));

    private readonly FailClosedDocumentStore store = new(storage);

    private readonly LocalAttemptStore local = new(clock);

    private static readonly Meter Meter = new(AuthMetrics.MeterName);

    private static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("ts_auth.rate_limit_fallback", description: "Rate-limit checks served from this replica's memory because storage failed.");

    // Fallback warnings are sampled to one a minute; the counter records every one.
    private long warnedAt = long.MinValue;

    // Bucket ids are keyed, so storage never reveals (or lets anyone test guesses of) the subject.
    private readonly byte[] idKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth rate-limit bucket ids"u8.ToArray());

    private readonly byte[] foldKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth rate-limit folds"u8.ToArray());

    /// <summary>
    /// Folds attacker-chosen text (an email address, an IP address) into one of 65,536 keyed subjects, so storage keeps
    /// a bounded number of buckets per purpose, and nobody can choose text that shares a known victim's bucket. A
    /// collision conservatively shares the budget.
    /// </summary>
    public string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var digest = HMACSHA256.HashData(this.foldKey, Encoding.UTF8.GetBytes(text));
        return $"fold-{System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(digest)}";
    }

    /// <summary>Charges one attempt in a one-minute window; see the overload.</summary>
    public Task<bool> TryAcquireAsync(string instanceId, string purpose, string subject, int maximum, CancellationToken cancellationToken) =>
        this.TryAcquireAsync(instanceId, purpose, subject, maximum, DefaultWindow, cancellationToken);

    /// <summary>Charges one attempt; returns whether it was admitted. See <see cref="AcquireAsync(string, string, string, int, TimeSpan, CancellationToken)"/>.</summary>
    public async Task<bool> TryAcquireAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, CancellationToken cancellationToken) =>
        await this.AcquireAsync(instanceId, purpose, subject, maximum, window, cancellationToken) is null;

    /// <summary>
    /// Charges one attempt to <paramref name="subject"/> for <paramref name="purpose"/> in the
    /// instance. Returns <see langword="null"/> when admitted; once <paramref name="maximum"/>
    /// attempts were admitted, returns the delay to the persisted window's end, rounded up to whole
    /// seconds (at least one), even when another replica's clock is ahead. Each distinct subject keeps
    /// at most three regional bucket documents, so the subject must be an identifier the service already holds (user, client); a caller that
    /// limits by attacker-chosen text must first <see cref="Fold"/> it. Windows are fixed
    /// multiples of <paramref name="window"/> (whole seconds); one purpose must always use the same window.
    /// </summary>
    public async Task<TimeSpan?> AcquireAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, CancellationToken cancellationToken) =>
        (await this.AcquireWithDeadlineAsync(instanceId, purpose, subject, maximum, window, cancellationToken))?.RetryAfter;

    /// <summary>Charges a fixed-window attempt, retaining the exact stored deadline for a lockout notice claim.</summary>
    public Task<AttemptRejection?> AcquireWithDeadlineAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, CancellationToken cancellationToken) =>
        this.AcquireAsync(instanceId, purpose, subject, maximum, window, cooldown: false, cancellationToken);

    /// <summary>Admits one cooldown attempt; returns whether it was admitted. See <see cref="AcquireCooldownAsync"/>.</summary>
    public async Task<bool> TryAcquireCooldownAsync(string instanceId, string purpose, string subject, TimeSpan interval, CancellationToken cancellationToken) =>
        await this.AcquireCooldownAsync(instanceId, purpose, subject, interval, cancellationToken) is null;

    /// <summary>
    /// Admits one attempt per elapsed <paramref name="interval"/>, measured from the last admission rather than fixed
    /// clock boundaries. Returns <see langword="null"/> when admitted or the remaining delay rounded up to whole seconds (at least one).
    /// Uses a separate purpose from fixed windows; attacker-chosen subjects must be folded.
    /// </summary>
    public async Task<TimeSpan?> AcquireCooldownAsync(string instanceId, string purpose, string subject, TimeSpan interval, CancellationToken cancellationToken) =>
        (await this.AcquireAsync(instanceId, purpose, subject, 1, interval, cooldown: true, cancellationToken))?.RetryAfter;

    /// <summary>
    /// Admits one request from a token bucket holding <paramref name="burst"/> tokens that regains one token per
    /// <paramref name="interval"/>. Each region stores its own outstanding debt (one interval per admission) and when
    /// it wrote it, by its own clock; a reader drains that debt only by the time it has itself seen elapse since then,
    /// so a replica whose clock is behind never inflates another region's debt. A request is admitted while the summed
    /// debt leaves room for one more interval. Refused requests change nothing, so retrying while limited never extends
    /// the wait. Returns <see langword="null"/> when admitted, otherwise the delay after which the debt has drained
    /// enough (an upper bound, rounded up to whole seconds, at least one). Uses its own purposes, never shared with
    /// fixed windows or cooldowns; attacker-chosen subjects must be folded.
    /// </summary>
    public Task<TimeSpan?> AcquireTokenAsync(string instanceId, string purpose, string subject, int burst, TimeSpan interval, CancellationToken cancellationToken) =>
        this.WithFallbackAsync(source => this.AcquireTokenAsync(source, instanceId, purpose, subject, burst, interval, cancellationToken), purpose, cancellationToken);

    private async Task<TimeSpan?> AcquireTokenAsync(IDocumentStore source, string instanceId, string purpose, string subject, int burst, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burst);
        var step = (long)interval.TotalMilliseconds;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step, nameof(interval));
        var capacity = checked(burst * step);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, int.MaxValue, nameof(burst));
        var keys = Regions.Select(component => this.BucketKey(purpose, component + ":" + subject)).ToArray();
        for (var attempt = 0; attempt < Math.Min(burst + 2L, MaxRounds); attempt++)
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var components = await Task.WhenAll(keys.Select(component => source.GetAsync<AttemptBucket>(Collection, instanceId, component, cancellationToken)));
            // Window: the writer's clock at the write, in Unix milliseconds. Used: its debt then, in milliseconds.
            if (components.Any(component => component is not null && (component.Document is not { Window: >= 0, Used: >= 0 }
                || component.Document.Used > capacity || component.Document.Window > now + MaxClockSkew)))
            {
                throw ApiException.Unavailable(new InvalidDataException("A stored token bucket holds an invalid write time or debt."));
            }

            var debts = components.Select(component => component is null ? 0
                : Math.Max(0L, component.Document.Used - Math.Max(0L, now - component.Document.Window))).ToArray();
            var debt = debts.Sum();
            if (debt + step > capacity)
            {
                return TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((debt + step - capacity) / 1000.0)));
            }

            var current = components[this.regionIndex];
            // Stamped with this replica's clock: a faster reader drains it early (admitting up to the skew), never locks out.
            var bucket = new AttemptBucket(now, (int)(debts[this.regionIndex] + step));
            if (current is null
                ? await source.TryCreateAsync(Collection, instanceId, keys[this.regionIndex], bucket, cancellationToken)
                : await source.TryReplaceAsync(Collection, instanceId, keys[this.regionIndex], bucket, current.Version, cancellationToken))
            {
                return null;
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// Claims one notice attempt for an actual account and authentication purpose at the persisted lockout deadline.
    /// Uses the existing collection and conditional writes; newer deadlines supersede older ones, so a slower replica
    /// cannot claim an old episode again. Call on the mail worker after account lookup, never with a folded account id.
    /// The claim prevents duplicate send attempts, not delivery loss if a sender or process fails after claiming.
    /// </summary>
    public async Task<bool> ClaimLockoutNoticeAsync(string instanceId, string purpose, string userId, DateTimeOffset until, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var key = this.BucketKey($"lockout-notice-{purpose}", userId);
        var notice = new AttemptBucket(until.ToUnixTimeSeconds(), 1);
        for (var round = 0; round < MaxRounds; round++)
        {
            if (until <= clock.GetUtcNow())
            {
                return false;
            }

            var current = await this.store.GetAsync<AttemptBucket>(Collection, instanceId, key, cancellationToken);
            if (until <= clock.GetUtcNow())
            {
                return false;
            }

            if (current is null)
            {
                if (await this.store.TryCreateAsync(Collection, instanceId, key, notice, cancellationToken))
                {
                    // An expired claim may remain persisted, but must never authorize a send.
                    return until > clock.GetUtcNow();
                }

                continue;
            }

            if (current.Document is not { Window: > 0, Used: 1 } || current.Document.Window > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            {
                throw ApiException.Unavailable(new InvalidDataException("A stored lockout notice claim holds an invalid deadline or count."));
            }

            if (current.Document.Window >= notice.Window)
            {
                return false;
            }

            if (await this.store.TryReplaceAsync(Collection, instanceId, key, notice, current.Version, cancellationToken))
            {
                return until > clock.GetUtcNow();
            }
        }

        throw ApiException.Unavailable();
    }

    private Task<AttemptRejection?> AcquireAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, bool cooldown, CancellationToken cancellationToken) =>
        this.WithFallbackAsync(source => this.AcquireAsync(source, instanceId, purpose, subject, maximum, window, cooldown, cancellationToken), purpose, cancellationToken);

    // Runs a check on TerraBase; when that storage fails (not when the caller gave up), runs it on this replica's memory.
    private async Task<T> WithFallbackAsync<T>(Func<IDocumentStore, Task<T>> check, string purpose, CancellationToken cancellationToken)
    {
        try
        {
            return await check(this.store).ConfigureAwait(false);
        }
        catch (ApiException failure) when (failure.Status == StatusCodes.Status503ServiceUnavailable && !cancellationToken.IsCancellationRequested
            && Transient(failure.InnerException))
        {
            Fallbacks.Add(1, new KeyValuePair<string, object?>("purpose", purpose));
            var now = clock.GetTimestamp();
            var last = Interlocked.Read(ref this.warnedAt);
            if ((last == long.MinValue || clock.GetElapsedTime(last, now) >= TimeSpan.FromMinutes(1))
                && Interlocked.CompareExchange(ref this.warnedAt, now, last) == last)
            {
                logger?.ZLogWarning(failure.InnerException ?? failure, $"Rate-limit storage failed; {purpose:@Purpose} and other budgets run on this replica's memory");
            }

            return await check(this.local).ConfigureAwait(false);
        }
    }

    // Storage that is down, slow or overloaded. A rejected credential, a missing collection, persistent contention or a
    // damaged bucket still fails closed: those are faults to fix, not outages to ride out.
    private static bool Transient(Exception? failure) => failure switch
    {
        TimeoutException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || status is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.MisdirectedRequest,
        ApiException { Status: StatusCodes.Status503ServiceUnavailable } inner => Transient(inner.InnerException),
        _ => false,
    };

    private async Task<AttemptRejection?> AcquireAsync(IDocumentStore source, string instanceId, string purpose, string subject, int maximum, TimeSpan window, bool cooldown, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        var windowSeconds = window.Ticks / TimeSpan.TicksPerSecond;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSeconds, nameof(window));
        var latestWindow = cooldown
            ? (DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks - window.Ticks) / TimeSpan.TicksPerMillisecond
            : DateTimeOffset.MaxValue.ToUnixTimeSeconds() / windowSeconds - 1;

        // shortcut: one TerraBase writer per region; use per-writer components before adding independent same-region writers.
        // The address is instance-scoped; service-chosen purposes never contain ':', and the subject comes last.
        var keys = Regions.Select(component => this.BucketKey(purpose, component + ":" + subject)).ToArray();
        var key = keys[this.regionIndex];

        // Every lost conditional write means another attempt was admitted (or the window rolled
        // over, or the bucket was created), so maximum + 2 rounds always reach a decision unless
        // storage itself keeps failing. MaxRounds bounds the work a large maximum would allow.
        for (var attempt = 0; attempt < Math.Min(maximum + 2L, MaxRounds); attempt++)
        {
            var now = clock.GetUtcNow();
            var number = cooldown ? now.ToUnixTimeMilliseconds() : now.ToUnixTimeSeconds() / windowSeconds;
            // Each serving region owns one component. Replication never merges two regions' increments into a
            // last-writer-wins integer, including when both regions create their first bucket during a partition.
            var components = await Task.WhenAll(keys.Select(component => source.GetAsync<AttemptBucket>(Collection, instanceId, component, cancellationToken)));
            foreach (var component in components)
            {
                if (component is not null && (component.Document is not { Window: >= 0, Used: >= 0 }
                    || component.Document.Window > latestWindow))
                {
                    throw ApiException.Unavailable(new InvalidDataException("A stored attempt bucket holds an invalid window timestamp or count."));
                }
            }

            var latest = components.Where(component => component is not null).Select(component => component!.Document.Window).DefaultIfEmpty(number).Max();
            var effective = cooldown
                ? (number - latest) * TimeSpan.TicksPerMillisecond >= window.Ticks ? number : latest
                : Math.Max(number, latest);
            var used = components.Where(component => component?.Document.Window == effective).Sum(component => (long)component!.Document.Used);
            if (used >= maximum)
            {
                var end = cooldown
                    ? DateTimeOffset.FromUnixTimeMilliseconds(effective) + window
                    : DateTimeOffset.FromUnixTimeSeconds((effective + 1) * windowSeconds);
                return new AttemptRejection(end, TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((end - clock.GetUtcNow()).TotalSeconds))));
            }

            var current = components[this.regionIndex];
            var bucket = new AttemptBucket(effective, current?.Document.Window == effective ? checked(current.Document.Used + 1) : 1);
            if (current is null
                ? await source.TryCreateAsync(Collection, instanceId, key, bucket, cancellationToken)
                : await source.TryReplaceAsync(Collection, instanceId, key, bucket, current.Version, cancellationToken))
            {
                return null;
            }
        }

        throw ApiException.Unavailable();
    }

    private string BucketKey(string purpose, string subject) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.idKey, Encoding.UTF8.GetBytes($"{purpose}:{subject}")));
}
