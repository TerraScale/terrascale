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
    public static Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what) => UntilAsync(read, done, what, Deadline);

    // The same with its own deadline: anti-entropy repairs within rounds of 60 s, not one replication delay.
    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, TimeSpan deadline)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var latest = await read();
            if (done(latest))
            {
                return latest;
            }

            if (clock.Elapsed > deadline)
            {
                throw new TimeoutException($"{what} did not converge within {deadline.TotalSeconds:F0} s; last read: {(object?)latest ?? "nothing"}");
            }

            await Task.Delay(Interval);
        }
    }

    // The document as each region serves it now.
    public static async Task<RegionCopies> BothAsync(Cluster cluster, Uri uri) =>
        new(await Docs.ReadAsync(cluster[Cluster.Gru], uri), await Docs.ReadAsync(cluster[Cluster.Sjc], uri));

    // The document as every region of the cluster serves it now.
    public static async Task<RegionSet> AllAsync(Cluster cluster, Uri uri)
    {
        var copies = await Task.WhenAll(cluster.Regions.Select(async region => (region, await Docs.ReadAsync(cluster[region], uri))));
        return new RegionSet(copies.ToDictionary(copy => copy.region, copy => copy.Item2, StringComparer.Ordinal));
    }

    // Until every region serves the same document, present unless absent is allowed.
    public static Task<RegionSet> SettledAsync(Cluster cluster, Uri uri, string what, bool absent = false) =>
        UntilAsync(() => AllAsync(cluster, uri), copies => copies.Identical && (absent || copies.Present), what);

    // Writes the seed through gru and waits until every region serves it.
    public static async Task SeedAllAsync(Cluster cluster, Uri uri, JsonObject body)
    {
        await Docs.PutAsync(cluster[Cluster.Gru], uri, body);
        _ = await SettledAsync(cluster, uri, "seed");
    }

    // Until every region lists the collection with this digest; answers the listings.
    public static Task<Dictionary<string, Listing>> ListedAsync(Cluster cluster, string expected, string what) =>
        UntilAsync(
            async () => (await Task.WhenAll(cluster.Regions.Select(async region => (region, await Docs.ListAsync(cluster[region], cluster)))))
                .ToDictionary(listing => listing.region, listing => listing.Item2, StringComparer.Ordinal),
            listings => listings.Values.All(listing => listing.Digest == expected), what);

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

/// <summary>Every region's copy of one document. A region without the document holds null.</summary>
internal sealed record RegionSet(IReadOnlyDictionary<string, JsonNode?> Copies)
{
    public bool Identical => Copies.Values.Select(Docs.Canonical).Distinct(StringComparer.Ordinal).Count() == 1;

    public bool Present => Copies.Values.All(copy => copy is not null);

    /// <summary>The copy the regions agree on (gru's).</summary>
    public JsonNode? Value => Copies[Cluster.Gru];

    public override string ToString() =>
        string.Join(' ', Copies.Select(copy => $"{copy.Key}={(copy.Value is null ? "absent" : Docs.Canonical(copy.Value))}"));
}

/// <summary>Both regions' copies of one document. A region without the document holds null.</summary>
internal sealed record RegionCopies(JsonNode? Gru, JsonNode? Sjc)
{
    public bool Identical => Docs.Canonical(Gru) == Docs.Canonical(Sjc);

    public override string ToString() => $"gru={(Gru is null ? "absent" : Docs.Canonical(Gru))} sjc={(Sjc is null ? "absent" : Docs.Canonical(Sjc))}";
}
