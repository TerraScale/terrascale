using System.Text;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Security;

namespace TerraScale.Auth.Features.Identity;

/// <summary>
/// Runs Argon2id (<see cref="Passwords"/>) off the request path, at most <see cref="Concurrency"/> at a time per
/// replica, so a burst of sign-ins can neither exhaust memory (19 MiB each) nor starve other requests; a request that
/// waits longer than <see cref="Wait"/> answers 503. This bounds local CPU only and decides no security outcome.
/// </summary>
public sealed class PasswordHasher : IDisposable
{
    /// <summary>Shortest accepted password, in UTF-8 bytes.</summary>
    public const int MinBytes = 8;

    /// <summary>Longest accepted password, in UTF-8 bytes.</summary>
    public const int MaxBytes = 1024;

    private const int Concurrency = 4;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly SemaphoreSlim slots = new(Concurrency);

    // Verified against when there is no real hash, so an unknown account costs what a real one does.
    private readonly Lazy<string> equalizer = new(() => Passwords.Hash("ts-auth sign-in timing equalizer"));

    /// <summary>Whether <paramref name="password"/> is well-formed UTF-16 of <see cref="MinBytes"/> to <see cref="MaxBytes"/> UTF-8 bytes.</summary>
    public static bool Acceptable(string? password)
    {
        try
        {
            return password is not null && StrictUtf8.GetByteCount(password) is >= MinBytes and <= MaxBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>A fresh Argon2id hash of <paramref name="password"/>.</summary>
    public Task<string> HashAsync(string password, CancellationToken cancellationToken) =>
        this.RunAsync(() => Passwords.Hash(password), cancellationToken);

    /// <summary>
    /// Whether <paramref name="password"/> matches <paramref name="hash"/>. Without a hash (no account, or no password)
    /// it spends the same work on a fixed hash and answers false.
    /// </summary>
    public Task<bool> VerifyAsync(string password, string? hash, CancellationToken cancellationToken) =>
        this.RunAsync(() => Passwords.Verify(password, hash ?? this.equalizer.Value) && hash is not null, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => this.slots.Dispose();

    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        if (!await this.slots.WaitAsync(Wait, cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(503, "temporarily_unavailable", "Authentication is busy; retry shortly.");
        }

        try
        {
            return await Task.Run(work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.slots.Release();
        }
    }
}
