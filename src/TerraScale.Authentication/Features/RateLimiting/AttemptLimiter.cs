using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.RateLimiting;

public readonly record struct AttemptRejection(DateTimeOffset Until, TimeSpan RetryAfter);

public sealed class AttemptLimiter(IAttemptStore store, TimeProvider clock, AuthOptions options, string region = "gru", ILogger<AttemptLimiter>? logger = null)
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    private const int MaxRounds = 64;

    private const long MaxClockSkew = 24 * 60 * 60 * 1000;

    private static readonly string[] Regions = ["gru", "sjc", "fra"];

    private readonly int regionIndex = Array.IndexOf(Regions, region) is var index && index >= 0
        ? index : throw new ArgumentException("The auth region must be gru, sjc or fra.", nameof(region));

    private readonly LocalAttemptStore local = new(clock);

    private static readonly Meter Meter = new(AuthMetrics.MeterName);

    private static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("ts_auth.rate_limit_fallback", description: "Rate-limit checks served from this replica's memory because storage failed.");

    private static readonly Counter<long> Refusals = Meter.CreateCounter<long>("ts_auth.rate_limit_refused", description: "Attempts refused because their budget was used up.");

    private long warnedAt = long.MinValue;

    private readonly byte[] idKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth rate-limit bucket ids"u8.ToArray());

    private readonly byte[] foldKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth rate-limit folds"u8.ToArray());

    public string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var digest = HMACSHA256.HashData(this.foldKey, Encoding.UTF8.GetBytes(text));
        return $"fold-{System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(digest)}";
    }

    public Task<bool> TryAcquireAsync(string instanceId, string purpose, string subject, int maximum, CancellationToken cancellationToken) =>
        this.TryAcquireAsync(instanceId, purpose, subject, maximum, DefaultWindow, cancellationToken);

    public async Task<bool> TryAcquireAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, CancellationToken cancellationToken) =>
        await this.AcquireAsync(instanceId, purpose, subject, maximum, window, cancellationToken) is null;

    public async Task<TimeSpan?> AcquireAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, CancellationToken cancellationToken) =>
        (await this.AcquireWithDeadlineAsync(instanceId, purpose, subject, maximum, window, cancellationToken))?.RetryAfter;

    public Task<AttemptRejection?> AcquireWithDeadlineAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, CancellationToken cancellationToken) =>
        this.AcquireAsync(instanceId, purpose, subject, maximum, window, cooldown: false, cancellationToken);

    public async Task<bool> TryAcquireCooldownAsync(string instanceId, string purpose, string subject, TimeSpan interval, CancellationToken cancellationToken) =>
        await this.AcquireCooldownAsync(instanceId, purpose, subject, interval, cancellationToken) is null;

    public async Task<TimeSpan?> AcquireCooldownAsync(string instanceId, string purpose, string subject, TimeSpan interval, CancellationToken cancellationToken) =>
        (await this.AcquireAsync(instanceId, purpose, subject, 1, interval, cooldown: true, cancellationToken))?.RetryAfter;

    public Task<TimeSpan?> AcquireTokenAsync(string instanceId, string purpose, string subject, int burst, TimeSpan interval, CancellationToken cancellationToken) =>
        this.WithFallbackAsync(source => this.AcquireTokenAsync(source, instanceId, purpose, subject, burst, interval, cancellationToken), purpose, cancellationToken);

    private async Task<TimeSpan?> AcquireTokenAsync(IAttemptStore source, string instanceId, string purpose, string subject, int burst, TimeSpan interval, CancellationToken cancellationToken)
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
            var components = await source.GetManyAsync(instanceId, keys, cancellationToken);
            if (components.Any(component => component is not null && (component.Value is not { Window: >= 0, Used: >= 0 }
                || component.Value.Used > capacity || component.Value.Window > now + MaxClockSkew)))
            {
                throw ApiException.Unavailable(new InvalidDataException("A stored token bucket holds an invalid write time or debt."));
            }

            var debts = components.Select(component => component is null ? 0
                : Math.Max(0L, component.Value.Used - Math.Max(0L, now - component.Value.Window))).ToArray();
            var debt = debts.Sum();
            if (debt + step > capacity)
            {
                Refusals.Add(1, new KeyValuePair<string, object?>("purpose", purpose));
                return TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((debt + step - capacity) / 1000.0)));
            }

            var current = components[this.regionIndex];
            var bucket = new AttemptBucket(now, (int)(debts[this.regionIndex] + step));
            if (current is null
                ? await source.TryCreateAsync(instanceId, keys[this.regionIndex], bucket, cancellationToken)
                : await source.TryReplaceAsync(instanceId, keys[this.regionIndex], bucket, current.Version, cancellationToken))
            {
                return null;
            }
        }

        throw ApiException.Unavailable();
    }

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

            var current = (await store.GetManyAsync(instanceId, [key], cancellationToken))[0];
            if (until <= clock.GetUtcNow())
            {
                return false;
            }

            if (current is null)
            {
                if (await store.TryCreateAsync(instanceId, key, notice, cancellationToken))
                {
                    return until > clock.GetUtcNow();
                }

                continue;
            }

            if (current.Value is not { Window: > 0, Used: 1 } || current.Value.Window > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            {
                throw ApiException.Unavailable(new InvalidDataException("A stored lockout notice claim holds an invalid deadline or count."));
            }

            if (current.Value.Window >= notice.Window)
            {
                return false;
            }

            if (await store.TryReplaceAsync(instanceId, key, notice, current.Version, cancellationToken))
            {
                return until > clock.GetUtcNow();
            }
        }

        throw ApiException.Unavailable();
    }

    public Task<TimeSpan?> AcquireTokenDurableAsync(string instanceId, string purpose, string subject, int burst, TimeSpan interval, CancellationToken cancellationToken) =>
        this.AcquireTokenAsync(store, instanceId, purpose, subject, burst, interval, cancellationToken);

    public async Task<TimeSpan?> AcquireDurableAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, CancellationToken cancellationToken) =>
        (await this.AcquireAsync(store, instanceId, purpose, subject, maximum, window, false, true, cancellationToken)).Rejection?.RetryAfter;

    public async Task<AttemptRejection?> CheckAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, CancellationToken cancellationToken) =>
        (await this.WithFallbackAsync(source => this.AcquireAsync(source, instanceId, purpose, subject, maximum, window, false, false, cancellationToken), purpose, cancellationToken)).Rejection;

    public async Task<(Reservation? Reservation, AttemptRejection? Rejection)> ReserveAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, CancellationToken cancellationToken)
    {
        var (admission, source) = await this.WithFallbackAsync(async source =>
            (await this.AcquireAsync(source, instanceId, purpose, subject, maximum, window, false, true, cancellationToken), source), purpose, cancellationToken);
        return admission.Rejection is { } rejection ? (null, rejection) : (new Reservation(this, source, instanceId, purpose, admission), null);
    }

    public sealed class Reservation
    {
        private AttemptLimiter? owner;
        private readonly IAttemptStore source;
        private readonly string instanceId;
        private readonly string purpose;
        private readonly Admission admission;

        internal Reservation(AttemptLimiter owner, IAttemptStore source, string instanceId, string purpose, Admission admission)
        {
            this.owner = owner;
            this.source = source;
            this.instanceId = instanceId;
            this.purpose = purpose;
            this.admission = admission;
        }

        public async Task ReturnAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref this.owner, null) is not { } limiter || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await AttemptLimiter.ReturnAsync(this.source, this.instanceId, this.admission, cancellationToken);
            }
            catch (Exception error)
            {
                limiter.LogReturnFailure(error, this.purpose);
            }
        }
    }

    private void LogReturnFailure(Exception error, string purpose) =>
        logger?.ZLogWarning(error, $"Could not return a successful primary authentication reservation for {purpose:@Purpose}; its charge is retained");

    private static async Task ReturnAsync(IAttemptStore source, string instanceId, Admission admission, CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxRounds; round++)
        {
            var current = (await source.GetManyAsync(instanceId, [admission.Key], cancellationToken))[0];
            if (current is null || current.Value.Window != admission.Window)
            {
                return;
            }

            if (current.Value.Used <= 0)
            {
                throw ApiException.Unavailable(new InvalidDataException("A reserved attempt has no stored charge."));
            }

            if (await source.TryReplaceAsync(instanceId, admission.Key, current.Value with { Used = current.Value.Used - 1 }, current.Version, cancellationToken))
            {
                return;
            }
        }

        throw ApiException.Unavailable();
    }

    private async Task<AttemptRejection?> AcquireAsync(string instanceId, string purpose, string subject, int maximum, TimeSpan window, bool cooldown, CancellationToken cancellationToken) =>
        (await this.WithFallbackAsync(source => this.AcquireAsync(source, instanceId, purpose, subject, maximum, window, cooldown, true, cancellationToken), purpose, cancellationToken)).Rejection;

    private async Task<T> WithFallbackAsync<T>(Func<IAttemptStore, Task<T>> check, string purpose, CancellationToken cancellationToken)
    {
        try
        {
            return await check(store);
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

            return await check(this.local);
        }
    }

    private static bool Transient(Exception? failure) => failure switch
    {
        TimeoutException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || status is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.MisdirectedRequest,
        ApiException { Status: StatusCodes.Status503ServiceUnavailable } inner => Transient(inner.InnerException),
        _ => false,
    };

    private async Task<Admission> AcquireAsync(IAttemptStore source, string instanceId, string purpose, string subject, int maximum, TimeSpan window, bool cooldown, bool charge, CancellationToken cancellationToken)
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

        var address = cooldown ? purpose : $"{purpose}/{windowSeconds}s";
        var keys = Regions.Select(component => this.BucketKey(address, component + ":" + subject)).ToArray();
        var key = keys[this.regionIndex];
        string[] others = [.. keys.Where((_, component) => component != this.regionIndex)];

        for (var attempt = 0; attempt < Math.Min(maximum + 2L, MaxRounds); attempt++)
        {
            var now = clock.GetUtcNow();
            var number = cooldown ? now.ToUnixTimeMilliseconds() : now.ToUnixTimeSeconds() / windowSeconds;
            var components = await source.GetManyAsync(instanceId, keys, cancellationToken);
            foreach (var component in components)
            {
                if (component is not null && (component.Value is not { Window: >= 0, Used: >= 0 }
                    || component.Value.Window > latestWindow))
                {
                    throw ApiException.Unavailable(new InvalidDataException("A stored attempt bucket holds an invalid window timestamp or count."));
                }
            }

            var latest = components.Where(component => component is not null).Select(component => component!.Value.Window).DefaultIfEmpty(number).Max();
            var effective = cooldown
                ? (number - latest) * TimeSpan.TicksPerMillisecond >= window.Ticks ? number : latest
                : Math.Max(number, latest);
            var used = components.Where(component => component?.Value.Window == effective).Sum(component => (long)component!.Value.Used);
            if (used >= maximum)
            {
                Refusals.Add(1, new KeyValuePair<string, object?>("purpose", purpose));
                var end = cooldown
                    ? DateTimeOffset.FromUnixTimeMilliseconds(effective) + window
                    : DateTimeOffset.FromUnixTimeSeconds((effective + 1) * windowSeconds);
                return new Admission(key, effective, new AttemptRejection(end, TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((end - clock.GetUtcNow()).TotalSeconds)))));
            }

            if (!charge)
            {
                return new Admission(key, effective, null);
            }

            var current = components[this.regionIndex];
            var own = current?.Value.Window == effective ? current.Value.Used : 0;
            if (current is null
                ? await source.TryCreateAsync(instanceId, key, new AttemptBucket(effective, 1), cancellationToken)
                : cooldown
                    ? await source.TryReplaceAsync(instanceId, key, new AttemptBucket(effective, checked(own + 1)), current.Version, cancellationToken)
                    : await source.TryChargeAsync(instanceId, key, others, effective, maximum, cancellationToken))
            {
                return new Admission(key, effective, null);
            }
        }

        throw ApiException.Unavailable();
    }

    internal readonly record struct Admission(string Key, long Window, AttemptRejection? Rejection);

    private string BucketKey(string purpose, string subject) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.idKey, Encoding.UTF8.GetBytes($"{purpose}:{subject}")));
}
