using System.Diagnostics;

namespace TerraBase.E2E;

internal static class Converge
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);
    public static Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, CancellationToken token = default) =>
        UntilAsync(read, done, what, Deadline, token);
    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, TimeSpan deadline, CancellationToken token = default)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var latest = await read();
            if (done(latest))
            {
                return latest;
            }

            if (clock.Elapsed > deadline)
            {
                throw new NotConvergedException($"{what} did not converge within {deadline.TotalSeconds:F0} s; last read: {(object?)latest ?? "nothing"}");
            }

            await Task.Delay(Interval, token);
        }
    }

    public static Task<Dictionary<string, Listing>> ListedAsync(Cluster cluster, string expected, string what, CancellationToken token = default) =>
        UntilAsync(
            async () => (await Task.WhenAll(Cluster.Regions.Select(async region => (region, await Docs.ListAsync(cluster[region], cluster, token)))))
                .ToDictionary(listing => listing.region, listing => listing.Item2, StringComparer.Ordinal),
            listings => listings.Values.All(listing => listing.Digest == expected), what, token);
}

internal sealed class NotConvergedException(string message) : TimeoutException(message);
