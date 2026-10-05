using System.Security.Cryptography;
using System.Text;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Store;

namespace TerraScale.Auth.Features.RateLimiting;

/// <summary>
/// Fixed one-minute attempt windows shared by every replica through conditional writes to
/// TerraBase, for authentication, recovery, verification and token endpoints. A replica with a
/// slower clock never resets a window another replica has already advanced. Storage failure or
/// persistent contention fails closed with 503; an attempt is never admitted unrecorded.
/// </summary>
public sealed class AttemptLimiter(IDocumentStore storage, TimeProvider clock, AuthOptions options)
{
    /// <summary>The TerraBase collection holding one <see cref="AttemptBucket"/> per limited subject.</summary>
    public const string Collection = "rate_limits";

    private const int WindowSeconds = 60;

    // More rounds than any realistic number of replicas racing on one subject, so only storage
    // that keeps rejecting writes (or a stampede beyond it) reaches the ceiling and fails closed.
    private const int MaxRounds = 64;

    private readonly FailClosedDocumentStore store = new(storage);

    // Bucket ids are keyed, so storage never reveals (or lets anyone test guesses of) the subject.
    private readonly byte[] idKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth rate-limit bucket ids"u8.ToArray());

    /// <summary>
    /// Charges one attempt to <paramref name="subject"/> for <paramref name="purpose"/> in the
    /// instance. Returns <see langword="false"/> once <paramref name="maximum"/> attempts were
    /// admitted in the current window. Each distinct subject keeps one bucket document, so the
    /// subject must be an identifier the service already holds (user, client); a caller that
    /// limits by attacker-chosen text must first fold it into a bounded set of buckets.
    /// </summary>
    public async Task<bool> TryAcquireAsync(string instanceId, string purpose, string subject, int maximum, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);

        // The address is scoped to the instance; purposes are service-chosen and never contain
        // ':', and the subject comes last.
        var key = Convert.ToHexStringLower(HMACSHA256.HashData(this.idKey, Encoding.UTF8.GetBytes($"{purpose}:{subject}")));

        // Every lost conditional write means another attempt was admitted (or the window rolled
        // over, or the bucket was created), so maximum + 2 rounds always reach a decision unless
        // storage itself keeps failing. MaxRounds bounds the work a large maximum would allow.
        for (var attempt = 0; attempt < Math.Min(maximum + 2L, MaxRounds); attempt++)
        {
            var window = clock.GetUtcNow().ToUnixTimeSeconds() / WindowSeconds;
            var current = await this.store.GetAsync<AttemptBucket>(Collection, instanceId, key, cancellationToken);
            if (current is null)
            {
                if (await this.store.TryCreateAsync(Collection, instanceId, key, new AttemptBucket(window, 1), cancellationToken))
                {
                    return true;
                }

                continue;
            }

            if (current.Document is not { Window: >= 0, Used: >= 0 })
            {
                throw ApiException.Unavailable(new InvalidDataException("A stored attempt bucket holds a negative window or count."));
            }

            var bucket = current.Document.Window < window ? new AttemptBucket(window, 0) : current.Document;
            if (bucket.Used >= maximum)
            {
                return false;
            }

            if (await this.store.TryReplaceAsync(Collection, instanceId, key, bucket with { Used = bucket.Used + 1 }, current.Version, cancellationToken))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }
}
