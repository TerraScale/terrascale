using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// The changes feed across regions (docs/terrabase/multi-region-replication.md section 14, slice R8): a cursor taken from
/// sjc's feed resumes on fra after sjc goes away, with every later write of gru and sjc exactly once and each origin's
/// events in its write order. Once with in-process nodes (or Fly), once with regions as separate OS processes and a
/// SIGKILL.
/// </summary>
public sealed class ChangesFeedE2ETests
{
    private const int Writes = 10;

    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task A_cursor_from_sjc_resumes_on_fra_after_sjc_stops_with_every_later_write_once_in_order()
    {
        await using var cluster = await Cluster.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        // Fly has no stop switch outside the fly-outage category: there sjc stays up and the cursor still moves to fra.
        await ResumeElsewhereAsync(region => cluster[region], cluster.DatabasePath, cluster.Collection, cluster.Document,
            Cluster.FlyRequested ? null : () => cluster.StopAsync(Cluster.Sjc));
    }

    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task Separate_processes_a_cursor_from_sjc_resumes_on_fra_after_sjc_is_killed_with_every_later_write_once_in_order()
    {
        Skip.When(Cluster.FlyRequested, "runs local node processes");
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        // The database is this test's own, so the feed needs no collection filter.
        await ResumeElsewhereAsync(region => regions[region], regions.DatabasePath, collection: null, regions.Document, () => regions.KillAsync(Cluster.Sjc));
    }

    // gru and sjc write; a consumer reads sjc's feed and keeps the cursor; gru and sjc write more, which reaches fra; sjc goes
    // away and gru writes again; the consumer resumes on fra from the cursor.
    private static async Task ResumeElsewhereAsync(Func<string, HttpClient> client, string databasePath, string? collection, Func<string, Uri> document, Func<Task>? stopSjc)
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

        using var fra = await Feed.OpenAsync(client(Cluster.Fra), changes + (collection is null ? "?" : "&") + $"cursor={Uri.EscapeDataString(cursor)}");
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
