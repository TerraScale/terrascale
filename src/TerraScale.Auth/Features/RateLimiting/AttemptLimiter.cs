using System.Security.Cryptography;
using System.Text;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Store;

namespace TerraScale.Auth.Features.RateLimiting;

/// <summary>A refused attempt's persisted deadline and remaining delay, rounded up to whole seconds.</summary>
/// <param name="Until">The stored window's end, shared by all replicas even when their clocks differ.</param>
/// <param name="RetryAfter">How long this replica must wait before retrying.</param>
public readonly record struct AttemptRejection(DateTimeOffset Until, TimeSpan RetryAfter);

/// <summary>
/// Fixed attempt windows (one minute unless a caller chooses another) and elapsed cooldowns shared by every replica through conditional writes to
/// TerraBase, for authentication, recovery, verification and token endpoints. A replica with a
/// slower clock never resets a window another replica has already advanced. Storage failure or
/// persistent contention fails closed with 503; an attempt is never admitted unrecorded.
/// </summary>
public sealed class AttemptLimiter(IDocumentStore storage, TimeProvider clock, AuthOptions options)
{
    /// <summary>The TerraBase collection holding one <see cref="AttemptBucket"/> per limited subject.</summary>
    public const string Collection = "rate_limits";

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    // More rounds than any realistic number of replicas racing on one subject, so only storage
    // that keeps rejecting writes (or a stampede beyond it) reaches the ceiling and fails closed.
    private const int MaxRounds = 64;

    private readonly FailClosedDocumentStore store = new(storage);

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
    /// one bucket document, so the subject must be an identifier the service already holds (user, client); a caller that
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

    private async Task<AttemptRejection?> AcquireAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, bool cooldown, CancellationToken cancellationToken)
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

        // The address is scoped to the instance; purposes are service-chosen and never contain
        // ':', and the subject comes last.
        var key = this.BucketKey(purpose, subject);

        // Every lost conditional write means another attempt was admitted (or the window rolled
        // over, or the bucket was created), so maximum + 2 rounds always reach a decision unless
        // storage itself keeps failing. MaxRounds bounds the work a large maximum would allow.
        for (var attempt = 0; attempt < Math.Min(maximum + 2L, MaxRounds); attempt++)
        {
            var now = clock.GetUtcNow();
            var number = cooldown ? now.ToUnixTimeMilliseconds() : now.ToUnixTimeSeconds() / windowSeconds;
            var current = await this.store.GetAsync<AttemptBucket>(Collection, instanceId, key, cancellationToken);
            if (current is null)
            {
                if (await this.store.TryCreateAsync(Collection, instanceId, key, new AttemptBucket(number, 1), cancellationToken))
                {
                    return null;
                }

                continue;
            }

            // Cooldowns store Unix milliseconds; fixed windows store window numbers. Bound each
            // so its end timestamp is representable before charging anything.
            if (current.Document is not { Window: >= 0, Used: >= 0 }
                || current.Document.Window > latestWindow)
            {
                throw ApiException.Unavailable(new InvalidDataException("A stored attempt bucket holds an invalid window timestamp or count."));
            }

            var expired = cooldown
                ? (number - current.Document.Window) * TimeSpan.TicksPerMillisecond >= window.Ticks
                : current.Document.Window < number;
            var bucket = expired ? new AttemptBucket(number, 0) : current.Document;
            if (bucket.Used >= maximum)
            {
                var end = cooldown
                    ? DateTimeOffset.FromUnixTimeMilliseconds(bucket.Window) + window
                    : DateTimeOffset.FromUnixTimeSeconds((bucket.Window + 1) * windowSeconds);
                return new AttemptRejection(end, TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((end - clock.GetUtcNow()).TotalSeconds))));
            }

            if (await this.store.TryReplaceAsync(Collection, instanceId, key, bucket with { Used = bucket.Used + 1 }, current.Version, cancellationToken))
            {
                return null;
            }
        }

        throw ApiException.Unavailable();
    }

    private string BucketKey(string purpose, string subject) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.idKey, Encoding.UTF8.GetBytes($"{purpose}:{subject}")));
}
