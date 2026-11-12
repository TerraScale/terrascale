using System.Threading.RateLimiting;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Security;

namespace TerraScale.Authentication.Features.Identity;

public sealed class PasswordHasher : IDisposable
{
    private const int Concurrency = 4;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim slots = new(Concurrency);
    private readonly PartitionedRateLimiter<string> tenants = PartitionedRateLimiter.Create<string, string>(tenant =>
        RateLimitPartition.GetConcurrencyLimiter(tenant, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 500,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        }));
    private readonly string equalizer = Passwords.Hash("ts-auth sign-in timing equalizer");

    public Task<string> HashAsync(string tenant, string password, CancellationToken cancellationToken) =>
        this.RunAsync(tenant, () => Passwords.Hash(password), cancellationToken);

    public Task<bool> VerifyAsync(string tenant, string password, string? hash, CancellationToken cancellationToken) =>
        this.RunAsync(tenant, () => ImportedPasswords.Verify(password, hash ?? this.equalizer) && hash is not null, cancellationToken);

    public void Dispose()
    {
        this.tenants.Dispose();
        this.slots.Dispose();
    }

    private async Task<T> RunAsync<T>(string tenant, Func<T> work, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Wait);
        try
        {
            using var share = await this.tenants.AcquireAsync(tenant, cancellationToken: deadline.Token).ConfigureAwait(false);
            if (!share.IsAcquired)
            {
                throw Busy();
            }

            await this.slots.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                return await Task.Run(work, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                this.slots.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Busy();
        }
    }

    private static ApiException Busy() => new(503, "temporarily_unavailable", "Authentication is busy; retry shortly.");
}
