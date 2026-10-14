using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// The changes feed across regions (docs/terrabase/multi-region-replication.md section 14, slice R8): a cursor taken from
/// sjc's feed resumes through fra with every later write of gru and sjc exactly once and each origin's events in its write
/// order: answered by sjc, its issuer, while sjc is up (locally and on Fly); answered by fra once sjc is stopped (in-process
/// nodes, and a pinned disposable app's sjc machine in the fly-outage run) or SIGKILLed (separate OS processes).
/// </summary>
public sealed class ChangesFeedE2ETests
{
    private const int Writes = 10;

    // sjc, the cursor's issuer, stops: fra's replay to it fails, and fra must serve the resume itself on its own prefix proof.
    // Local nodes only: the deploy run never stops a machine; on Fly this is the fly-outage scenario below.
    [Test]
    [Category("local-only")]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task A_cursor_from_sjc_resumes_on_fra_after_sjc_stops_with_every_later_write_once_in_order()
    {
        // Before the cluster starts: an unfiltered Fly run must fail here, never stop a machine or skip as if it passed.
        if (Cluster.FlyRequested)
        {
            throw new InvalidOperationException("stops the cursor's issuer: local nodes only; on Fly this is the fly-outage scenario");
        }

        await using var cluster = await Cluster.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        using var fra = await cluster.ViaAsync(Cluster.Fra, Cluster.Fra);
        await ResumeElsewhereAsync(region => cluster[region], fra, cluster.DatabasePath, cluster.Collection, cluster.Document, () => cluster.StopAsync(Cluster.Sjc));
        Expected.Check(cluster.Replays.Any(replay => replay is { Edge: Cluster.Fra, Target: Cluster.Sjc, FellBack: true } && replay.PathAndQuery.Contains("cursor=", StringComparison.Ordinal)),
            $"fra replayed the resume to sjc, its issuer, and served it after the replay failed; replays: {string.Join("; ", cluster.Replays)}");
    }

    // sjc, the cursor's issuer, stays up: the resume presented in fra is replayed to sjc (issuer routing), and only sjc's
    // answer counts. Locally and on Fly.
    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task A_cursor_from_sjc_presented_in_fra_is_replayed_to_sjc_with_every_later_write_once_in_order()
    {
        await using var cluster = await Cluster.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        using var resume = await cluster.ViaAsync(Cluster.Fra, Cluster.Sjc);
        await ResumeElsewhereAsync(region => cluster[region], resume, cluster.DatabasePath, cluster.Collection, cluster.Document, stopSjc: null);
    }

    // The issuer-stop scenario on a pinned disposable app: the sjc machine is stopped, and the fra machine (MachinePin) must
    // answer the resume itself. It stops a machine, so it runs only in make terrabase-e2e-fly-outage.
    [Test]
    [Category("fly-outage")]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task Fly_a_cursor_from_sjc_resumes_on_fra_after_the_sjc_machine_stops_with_every_later_write_once_in_order()
    {
        Skip.Unless(Cluster.FlyRequested && Cluster.FlyMachineFaults,
            "Fly outage test: a pinned disposable app (TERRABASE_E2E_FLY_URL, TERRABASE_E2E_FLY_APP, TERRABASE_E2E_FLY_REVISION) and TERRABASE_E2E_FLY_OUTAGE=1");
        await using var cluster = await Cluster.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        var stopped = false;
        try
        {
            await ResumeElsewhereAsync(region => cluster[region], cluster[Cluster.Fra], cluster.DatabasePath, cluster.Collection, cluster.Document, async () =>
            {
                await cluster.StopAsync(Cluster.Sjc);
                stopped = true;
            });
        }
        finally
        {
            if (stopped)
            {
                await cluster.StartAgainAsync(Cluster.Sjc);
            }
        }
    }

    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task Separate_processes_a_cursor_from_sjc_resumes_on_fra_after_sjc_is_killed_with_every_later_write_once_in_order()
    {
        Skip.When(Cluster.FlyRequested, "runs local node processes");
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        // The database is this test's own, so the feed needs no collection filter.
        await ResumeElsewhereAsync(region => regions[region], regions[Cluster.Fra], regions.DatabasePath, collection: null, regions.Document, () => regions.KillAsync(Cluster.Sjc));
    }

    // gru and sjc write; a consumer reads sjc's feed and keeps the cursor; gru and sjc write more, which reaches fra; sjc goes
    // away (`stopSjc`, when given) and gru writes again; the consumer resumes through `resume` from the cursor.
    private static async Task ResumeElsewhereAsync(Func<string, HttpClient> client, HttpClient resume, string databasePath, string? collection, Func<string, Uri> document, Func<Task>? stopSjc)
    {
        var before = await WriteAsync(client, document, 0, Writes, Cluster.Gru, Cluster.Sjc);
        var changes = $"{databasePath}/changes" + (collection is null ? "" : $"?collections={collection}");
        string cursor;
        using (var sjc = await Feed.OpenAsync(client(Cluster.Sjc), changes))
        {
            var seen = await sjc.UntilAsync(events => before.Keys.All(id => events.Any(change => change.DocumentId == id)));
            Expected.Check(seen.Count == before.Count, $"sjc's feed from the start holds each earlier write once; got {seen.Count} events for {before.Count} writes");
            cursor = seen[^1].Cursor;
        }

        var after = await WriteAsync(client, document, Writes, Writes, Cluster.Gru, Cluster.Sjc);
        foreach (var (id, write) in after)
        {
            _ = await Converge.UntilAsync(() => Docs.ReadAsync(client(Cluster.Fra), document(id)), body => body?["step"]?.GetValue<int>() == write.Step, $"{write.Region}'s write on fra");
        }

        if (stopSjc is not null)
        {
            await stopSjc();
        }

        foreach (var (id, write) in await WriteAsync(client, document, 2 * Writes, Writes / 2, Cluster.Gru))
        {
            after[id] = write;
        }

        using var fra = await Feed.OpenAsync(resume, changes + (collection is null ? "?" : "&") + $"cursor={Uri.EscapeDataString(cursor)}");
        var resumed = await fra.UntilAsync(events => after.Keys.All(id => events.Any(change => change.DocumentId == id)));
        // The next heartbeat follows the page that held them: nothing else was waiting.
        var trailing = await fra.UntilHeartbeatAsync();
        var delivered = resumed.Concat(trailing).ToList();
        Expected.Check(delivered.All(change => after.ContainsKey(change.DocumentId)), $"no write from before the cursor; got {string.Join(',', delivered.Where(change => !after.ContainsKey(change.DocumentId)).Select(change => change.DocumentId))}");
        Expected.Check(delivered.Count == after.Count, $"each later write exactly once; {delivered.Count} events for {after.Count} writes");
        foreach (var origin in delivered.GroupBy(change => change.Origin))
        {
            var steps = origin.Select(change => after[change.DocumentId].Step).ToArray();
            Expected.Check(steps.SequenceEqual(steps.Order()), $"origin {origin.Key} in write order; got {string.Join(',', steps)}");
            Expected.Check(origin.Select(change => after[change.DocumentId].Region).Distinct().Count() == 1, $"origin {origin.Key} is one region");
        }

        Expected.Check(delivered.Select(change => change.Origin).Distinct().Count() == 2, "gru's and sjc's writes come from their own origins");
        Expected.Check(delivered.All(change => change.Body?["step"]?.GetValue<int>() == after[change.DocumentId].Step), "each event carries its document as written");
    }

    private sealed record Write(string Region, int Step);

    // `count` writes of new documents, alternating between `regions`, one after another; a write's step is its position
    // in the whole test, from `first`.
    private static async Task<Dictionary<string, Write>> WriteAsync(Func<string, HttpClient> client, Func<string, Uri> document, int first, int count, params string[] regions)
    {
        var writes = new Dictionary<string, Write>(StringComparer.Ordinal);
        for (var step = first; step < first + count; step++)
        {
            var id = EntityIds.New().ToString();
            var region = regions[step % regions.Length];
            await Docs.PutAsync(client(region), document(id), new JsonObject { ["step"] = step });
            writes[id] = new Write(region, step);
        }

        return writes;
    }

    private sealed record Change(string Cursor, string DocumentId, string Origin, JsonNode? Body);

    /// <summary>One open SSE response of the changes feed.</summary>
    private sealed class Feed : IDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;

        private Feed(HttpResponseMessage response, StreamReader reader)
        {
            _response = response;
            _reader = reader;
        }

        public static async Task<Feed> OpenAsync(HttpClient client, string uri)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.Relative))
            {
                // A region process's internal listener speaks h2c only.
                Version = client.DefaultRequestVersion,
                VersionPolicy = client.DefaultVersionPolicy,
            };
            request.Headers.Accept.ParseAdd("text/event-stream");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var text = await response.Content.ReadAsStringAsync();
                response.Dispose();
                throw new HttpRequestException($"the changes feed answered {(int)response.StatusCode}: {text}", null, response.StatusCode);
            }

            return new Feed(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
        }

        // Change events until `enough` accepts them.
        public async Task<List<Change>> UntilAsync(Func<List<Change>, bool> enough)
        {
            using var deadline = new CancellationTokenSource(Converge.Deadline);
            var changes = new List<Change>();
            while (!enough(changes))
            {
                if (await NextAsync(deadline.Token) is { } change)
                {
                    changes.Add(change);
                }
            }

            return changes;
        }

        // Change events until the next heartbeat.
        public async Task<List<Change>> UntilHeartbeatAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var changes = new List<Change>();
            while (await NextAsync(deadline.Token) is { } change)
            {
                changes.Add(change);
            }

            return changes;
        }

        // The next change event, or null for a cursor event.
        private async Task<Change?> NextAsync(CancellationToken cancellationToken)
        {
            string? name = null, id = null, data = null;
            while (await _reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    if (name == "error")
                    {
                        throw new InvalidOperationException($"the changes feed failed: {data}");
                    }

                    if (name != "change")
                    {
                        return null;
                    }

                    var change = JsonNode.Parse(data!)!;
                    return new Change(id!, change["id"]!.GetValue<string>(), change["origin"]!.GetValue<string>(), change["document"]);
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var value = line[(colon + 1)..].TrimStart(' ');
                switch (line[..colon])
                {
                    case "event":
                        name = value;
                        break;
                    case "id":
                        id = value;
                        break;
                    case "data":
                        data += value;
                        break;
                }
            }

            throw new EndOfStreamException("the changes feed ended");
        }

        public void Dispose()
        {
            _reader.Dispose();
            _response.Dispose();
        }
    }
}
