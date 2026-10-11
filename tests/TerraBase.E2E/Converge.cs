using System.Diagnostics;
using System.Text.Json.Nodes;

namespace TerraBase.E2E;

/// <summary>Polling until replication has converged, and the latency reports.</summary>
internal static class Converge
{
    // Replication is asynchronous; the Fly path crosses regions over the public proxy.
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    // Reads again until done accepts the latest read. Past the deadline it fails and names that read.
    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var latest = await read();
            if (done(latest))
            {
                return latest;
            }

            if (clock.Elapsed > Deadline)
            {
                throw new TimeoutException($"{what} did not converge within {Deadline.TotalSeconds:F0} s; last read: {(object?)latest ?? "nothing"}");
            }

            await Task.Delay(Interval);
        }
    }

    // The document as each region serves it now.
    public static async Task<RegionCopies> BothAsync(Cluster cluster, Uri uri) =>
        new(await Docs.ReadAsync(cluster[Cluster.Gru], uri), await Docs.ReadAsync(cluster[Cluster.Sjc], uri));

    // Writes the seed through gru and waits until both regions serve it.
    public static async Task SeedAsync(Cluster cluster, Uri uri, JsonObject body)
    {
        await Docs.PutAsync(cluster[Cluster.Gru], uri, body);
        _ = await UntilAsync(() => BothAsync(cluster, uri), copies => copies.Identical && copies.Gru is not null, "seed");
    }

    // Logs how long replication took, measured from the moment the writes were acknowledged.
    public static void Report(string scenario, long acknowledged) =>
        Console.WriteLine($"replication latency, {scenario}: {Stopwatch.GetElapsedTime(acknowledged).TotalMilliseconds:F0} ms");
}

/// <summary>Both regions' copies of one document. A region without the document holds null.</summary>
internal sealed record RegionCopies(JsonNode? Gru, JsonNode? Sjc)
{
    public bool Identical => Docs.Canonical(Gru) == Docs.Canonical(Sjc);

    public override string ToString() => $"gru={(Gru is null ? "absent" : Docs.Canonical(Gru))} sjc={(Sjc is null ? "absent" : Docs.Canonical(Sjc))}";
}
