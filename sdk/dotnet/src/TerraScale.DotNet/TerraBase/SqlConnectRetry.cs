using System.Net;

namespace TerraScale.DotNet.TerraBase;

internal static class SqlConnectRetry
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan DelayCap = TimeSpan.FromSeconds(30);

    internal static async Task WaitAsync(TerraBaseException refusal, int attempt, TimeSpan remaining, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attempt >= MaxAttempts || refusal.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) || refusal.RetryAfter is not { } retryAfter)
            throw refusal;
        var delay = retryAfter < DelayCap ? retryAfter : DelayCap;
        if (delay >= remaining) throw refusal;
        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
    }
}
