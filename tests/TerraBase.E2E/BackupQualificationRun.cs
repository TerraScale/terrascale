using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogus;
using TerraBase.Features.Documents;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using Contents = TerraBase.E2E.BackupQualification.Contents;
using Cut = TerraBase.E2E.BackupQualification.Cut;
using Outcome = TerraBase.E2E.BackupQualification.Outcome;

namespace TerraBase.E2E;

/// <summary>Real HTTP backup and restore results; the same producer runs locally and on the pinned disposable app.</summary>
internal static class BackupQualificationRun
{
    internal sealed record Document(string Body, string Version, long Stamp);
    internal sealed record Run(IReadOnlyList<Cut> Plan, IReadOnlyList<Outcome> Outcomes, IReadOnlyList<string> Nodes, IReadOnlyDictionary<string, DateTimeOffset> PointInTimes);

    public static async Task<Run> ExecuteAsync(Func<Cluster, HttpClient, string, Task>? inspectRestored = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        await using var cluster = await Cluster.StartBackupAsync();
        var client = cluster[Cluster.Gru];
        var cleanup = new List<string>();
        var errors = new List<Exception>();
        Run? result = null;
        var plan = new List<Cut>();
        var outcomes = new List<Outcome>();
        var points = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            // Cut names and support status are frozen before writes, never selected from successful restores.
            string[] cuts = ["manual-after-commit", "pitr-checkpoint", "pitr-latest", "before-window"];
            var source = await GetAsync(client, cluster.DatabasePath, token);
            var organization = cluster.OrganizationId ?? throw new InvalidOperationException("backup qualification needs the source organization id");
            var sourceId = source.GetProperty("id").GetString()!;
            // Placement is frozen from the source record before any restore: a restore keeps the source's primary and replicas.
            string[] placement = [source.GetProperty("homeRegion").GetString()!,
                .. source.GetProperty("replicaRegions").EnumerateArray().Select(region => region.GetString()!)];
            var projectId = cluster.ProjectPath.Split('/')[^1];
            var acknowledged = new Dictionary<string, string>(StringComparer.Ordinal);
            var acknowledgedVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            var fake = new Faker();
            foreach (var region in cluster.Regions)
            {
                var ops = Enumerable.Range(0, 3).Select(_ =>
                {
                    var id = EntityIds.New().ToString();
                    var body = new JsonObject { ["region"] = region, ["text"] = fake.Lorem.Sentence() };
                    acknowledged.Add(id, Docs.Canonical(body));
                    return new { op = "put", collection = cluster.Collection, id, document = body };
                }).ToArray();
                using var committed = await cluster[region].PostAsJsonAsync(Uri(cluster.DatabasePath + "/transactions"), new { ops }, token);
                await RecordAcknowledgementsAsync(committed, acknowledgedVersions, token);
            }

            var digest = Docs.Digest(acknowledged.Select(pair => (pair.Key, pair.Value)));
            _ = await Converge.ListedAsync(cluster, digest, "acknowledged writes before backup");
            var firstCommitted = await CheckpointMarkerAsync(cluster, acknowledged, acknowledgedVersions, token);
            var initial = await CaptureVersionsAsync(client, cluster, organization, projectId, sourceId, acknowledged, acknowledgedVersions, token);
            using var backup = await client.PostAsync(Uri(cluster.DatabasePath + "/backups"), content: null, token);
            await Docs.EnsureSuccessAsync(backup);
            var backupBody = await backup.Content.ReadFromJsonAsync<JsonElement>(token);
            if (backupBody.GetProperty("status").GetString() != "ready" || backupBody.GetProperty("databaseId").GetString() != sourceId)
            {
                throw new InvalidOperationException("manual backup did not report ready for the source database");
            }

            // A shipped checkpoint past every member's server clock after the marker committed, not the runner's clock, chooses the PIT cut.
            var checkpoint = await ShippedAsync(cluster, firstCommitted, token);
            var first = Expected(initial, sourceId, placement);
            var changed = acknowledged.Keys.First();
            var update = new JsonObject { ["region"] = Cluster.Gru, ["text"] = fake.Lorem.Sentence() };
            acknowledged[changed] = Docs.Canonical(update);
            var later = EntityIds.New().ToString();
            var laterBody = new JsonObject { ["later"] = true };
            acknowledged.Add(later, Docs.Canonical(laterBody));
            using var committedLater = await client.PostAsJsonAsync(Uri(cluster.DatabasePath + "/transactions"), new
            {
                ops = new[]
                {
                    new { op = "put", collection = cluster.Collection, id = changed, document = update },
                    new { op = "put", collection = cluster.Collection, id = later, document = laterBody },
                },
            }, token);
            await RecordAcknowledgementsAsync(committedLater, acknowledgedVersions, token);
            _ = await Converge.ListedAsync(cluster, Docs.Digest(acknowledged.Select(pair => (pair.Key, pair.Value))), "later writes");
            var latestCommitted = await CheckpointMarkerAsync(cluster, acknowledged, acknowledgedVersions, token);
            var latestDocuments = await CaptureVersionsAsync(client, cluster, organization, projectId, sourceId, acknowledged, acknowledgedVersions, token);
            var latest = await ShippedAsync(cluster, latestCommitted, token);
            plan.AddRange([
                new(cuts[0], true, first), new(cuts[1], true, first), new(cuts[2], true, Expected(latestDocuments, sourceId, placement)),
                // Before the oldest snapshot yet inside the plan's retention window: the backup does not hold it, a typed 409.
                new(cuts[3], false, first, "backup_history_incomplete", 409),
            ]);
            foreach (var cut in plan)
            {
                var name = "e2e-restore-" + EntityIds.New().ToString().ToLowerInvariant();
                var path = cluster.ProjectPath + "/databases/" + name;
                // Record before POST: even a response lost after publication must leave a cleanup attempt.
                cleanup.Add(path);
                var route = cut.Name == cuts[0]
                    ? cluster.DatabasePath + "/backups/" + backupBody.GetProperty("id").GetString() + "/restore"
                    : cluster.DatabasePath + "/point-in-time/restore";
                var at = cut.Name == cuts[3] ? checkpoint.GetProperty("earliest").GetDateTimeOffset().AddMinutes(-1)
                    : cut.Name == cuts[2] ? latest.GetProperty("latest").GetDateTimeOffset() : checkpoint.GetProperty("latest").GetDateTimeOffset();
                if (cut.Name != cuts[0])
                {
                    points.Add(cut.Name, at);
                }

                var replays = cluster.Replays.Count;
                using var restored = await client.PostAsJsonAsync(Uri(route), new { name, pointInTime = at }, token);
                // The copy is made where the edge replayed the restore (the snapshot's region, or the primary for PITR);
                // its other regions fill asynchronously, so it is read there.
                var holder = cluster[cluster.Replays.Skip(replays).LastOrDefault(replay => !replay.FellBack && replay.PathAndQuery == route)?.Target
                    ?? Cluster.Gru];
                if (restored.StatusCode != HttpStatusCode.Created)
                {
                    var refusal = await restored.Content.ReadFromJsonAsync<JsonElement>(token);
                    var code = refusal.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var value) ? value.GetString() : null;
                    outcomes.Add(new(cut.Name, null, code, (int)restored.StatusCode));
                }
                else if (!cut.Supported)
                {
                    // This cut exercises the API's pre-window refusal, even a matching restore violates that contract.
                    outcomes.Add(new(cut.Name, null, null, (int)restored.StatusCode));
                }
                else
                {
                    if (inspectRestored is not null)
                    {
                        await inspectRestored(cluster, holder, path);
                    }

                    var publication = await restored.Content.ReadFromJsonAsync<JsonElement>(token);
                    var record = await GetAsync(holder, path, token);
                    if (record.GetProperty("id").GetString() == sourceId
                        || publication.GetProperty("id").GetString() != record.GetProperty("id").GetString()
                        || record.GetProperty("name").GetString() != name || record.GetProperty("projectId").GetString() != projectId)
                    {
                        throw new InvalidOperationException("restore did not publish the named new database in the source project");
                    }
                    var collections = await GetAsync(holder, path + "/collections", token);
                    if (!collections.GetProperty("items").EnumerateArray().Select(collection => collection.GetProperty("name").GetString())
                        .SequenceEqual([cluster.Collection]))
                    {
                        throw new InvalidOperationException("restored database has missing or unexpected collections");
                    }

                    var documents = await ListAsync(holder, path, cluster.Collection, token);
                    var original = cut.Name == cuts[2] ? latestDocuments : initial;
                    var scope = new TenantContext(organization, PlanTier.Enterprise, projectId, record.GetProperty("id").GetString()).VersionScope();
                    var versions = documents.ToDictionary(pair => pair.Key, pair => original.TryGetValue(pair.Key, out var expected)
                        && pair.Value.Version == ETags.Version(scope, cluster.Collection, pair.Key, expected.Stamp, System.Text.Encoding.UTF8.GetBytes(expected.Body))
                            ? expected.Version : "different:" + pair.Value.Version, StringComparer.Ordinal);
                    var regions = new[] { record.GetProperty("homeRegion").GetString()! }
                        .Concat(record.GetProperty("replicaRegions").EnumerateArray().Select(region => region.GetString()!)).ToArray();
                    outcomes.Add(new(cut.Name, new(documents.Count, Docs.Digest(documents.Select(pair => (pair.Key, Docs.Canonical(JsonNode.Parse(pair.Value.Body))))),
                        versions, record.GetProperty("branchedFrom").GetString()!, regions), null, (int)restored.StatusCode, record.GetProperty("id").GetString()));
                }

                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    backup_cut = cut.Name,
                    supported = cut.Supported,
                    outcome = outcomes[^1],
                    expected = cut.Acknowledged,
                    point_in_time = cut.Name == cuts[0] ? (DateTimeOffset?)null : at,
                    nodes = cluster.Regions.Select(cluster.Identity).ToArray(),
                }));
            }

            result = new(plan, outcomes, [.. cluster.Regions.Select(cluster.Identity)], points);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        finally
        {
            foreach (var path in cleanup)
            {
                try
                {
                    // A restore inherits the source's replica regions, so it is deleted the way every owned database is.
                    await Cluster.DeleteDatabaseAsync(region => cluster[region], Cluster.Gru, path);
                }
                catch (Exception cleanupError)
                {
                    errors.Add(cleanupError);
                }
            }
        }

        if (errors.Count > 0)
        {
            Outcomes.Mark("fail", JsonSerializer.Serialize(new
            {
                backup = new Run(plan, outcomes, [.. cluster.Regions.Select(cluster.Identity)], points),
                errors = errors.Select(error => error.Message).ToArray(),
            }));
        }

        return errors.Count == 0 ? result! : throw new AggregateException("backup qualification or cleanup failed", errors);
    }

    // Every member already holds the earlier writes before this marker can reach whichever member ships the backup. Answers
    // a restore point at or after which every member's copy of the marker is restorable.
    private static async Task<DateTimeOffset> CheckpointMarkerAsync(Cluster cluster, Dictionary<string, string> bodies, Dictionary<string, string> versions, CancellationToken token)
    {
        var id = EntityIds.New().ToString();
        var body = new JsonObject { ["checkpoint"] = id };
        bodies.Add(id, Docs.Canonical(body));
        using var committed = await cluster[Cluster.Gru].PostAsJsonAsync(Uri(cluster.DatabasePath + "/transactions"), new
        {
            ops = new[] { new { op = "put", collection = cluster.Collection, id, document = body } },
        }, token);
        await RecordAcknowledgementsAsync(committed, versions, token);
        _ = await Converge.ListedAsync(cluster, Docs.Digest(bodies.Select(pair => (pair.Key, pair.Value))), "checkpoint marker", token);
        // The shipper stamps its commit row with its own wall clock in whole seconds, not with the marker's HLC version, and
        // a restore at T holds it only once that second ends by T. Kestrel's Date is a cache its heartbeat refreshes every
        // second, so a served Date can predate the commit; one that changes after the marker was listed everywhere was set
        // later than that member's commit, and the second after it ends the commit's second.
        var dates = await Task.WhenAll(cluster.Regions.Select(async region =>
        {
            var served = await ServedAtAsync(cluster[region], cluster.DatabasePath, token);
            return await Converge.UntilAsync(() => ServedAtAsync(cluster[region], cluster.DatabasePath, token), date => date > served, $"a refreshed {region} server Date", token);
        }));
        return dates.Max().AddSeconds(1);
    }

    private static async Task<DateTimeOffset> ServedAtAsync(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.GetAsync(Uri(path), token);
        await Docs.EnsureSuccessAsync(response);
        return response.Headers.Date ?? throw new InvalidOperationException("a member answered without a Date header");
    }

    private static async Task RecordAcknowledgementsAsync(HttpResponseMessage committed, Dictionary<string, string> versions, CancellationToken token)
    {
        await Docs.EnsureSuccessAsync(committed);
        var transaction = await committed.Content.ReadFromJsonAsync<JsonElement>(token);
        if (transaction.GetProperty("durability").GetString() != "remote_durable")
        {
            throw new InvalidOperationException("transaction was not acknowledged as remote_durable");
        }

        foreach (var document in transaction.GetProperty("results").EnumerateArray())
        {
            versions[document.GetProperty("id").GetString()!] = document.GetProperty("version").GetString()
                ?? throw new InvalidOperationException("an acknowledged write has no version");
        }
    }

    private static Contents Expected(Dictionary<string, Document> documents, string sourceId, string[] placement) => new(documents.Count,
        Docs.Digest(documents.Select(pair => (pair.Key, Docs.Canonical(JsonNode.Parse(pair.Value.Body))))),
        documents.ToDictionary(pair => pair.Key, pair => pair.Value.Version, StringComparer.Ordinal), sourceId, placement);

    private static async Task<JsonElement> ShippedAsync(Cluster cluster, DateTimeOffset from, CancellationToken token) => await Converge.UntilAsync(async () =>
    {
        await cluster.ShipBackupAsync(token);
        using var window = await cluster[Cluster.Gru].GetAsync(Uri(cluster.DatabasePath + "/point-in-time"), token);
        if (window.StatusCode == HttpStatusCode.NotFound)
        {
            return (JsonElement?)null;
        }

        await Docs.EnsureSuccessAsync(window);
        return await window.Content.ReadFromJsonAsync<JsonElement>(token);
    }, window => window is { } found && found.GetProperty("latest").GetDateTimeOffset() >= from,
        "a newer shipped backup checkpoint", token) is { } shipped ? shipped : throw new InvalidOperationException("no shipped checkpoint");

    private static async Task<Dictionary<string, Document>> CaptureVersionsAsync(HttpClient client, Cluster cluster, string organization, string project, string database,
        Dictionary<string, string> acknowledged, Dictionary<string, string> acknowledgedVersions, CancellationToken token)
    {
        var documents = await ListAsync(client, cluster.DatabasePath, cluster.Collection, token);
        if (documents.Count != acknowledged.Count || documents.Any(pair => !acknowledged.TryGetValue(pair.Key, out var body) || body != Docs.Canonical(JsonNode.Parse(pair.Value.Body))
            || !acknowledgedVersions.TryGetValue(pair.Key, out var version) || version != pair.Value.Version))
        {
            throw new InvalidOperationException("source listing differs from the acknowledged writes");
        }

        var scope = new TenantContext(organization, PlanTier.Enterprise, project, database).VersionScope();
        var remaining = documents.Keys.ToHashSet(StringComparer.Ordinal);
        using var response = await client.GetAsync(Uri(cluster.DatabasePath + "/changes?collections=" + cluster.Collection), HttpCompletionOption.ResponseHeadersRead, token);
        await Docs.EnsureSuccessAsync(response);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        while (remaining.Count > 0)
        {
            var line = await reader.ReadLineAsync(token) ?? throw new InvalidOperationException("changes ended before every acknowledged version was observed");
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            using var json = JsonDocument.Parse(line[6..]);
            if (!json.RootElement.TryGetProperty("id", out var idValue) || idValue.GetString() is not { } id || !remaining.Contains(id))
            {
                continue;
            }

            var stamp = long.Parse(json.RootElement.GetProperty("version").GetString()!, CultureInfo.InvariantCulture);
            var document = documents[id];
            if (ETags.Version(scope, cluster.Collection, id, stamp, System.Text.Encoding.UTF8.GetBytes(document.Body)) == document.Version)
            {
                documents[id] = document with { Stamp = stamp };
                remaining.Remove(id);
            }
        }

        return documents;
    }

    internal static async Task<Dictionary<string, Document>> ListAsync(HttpClient client, string path, string collection, CancellationToken token)
    {
        var documents = new Dictionary<string, Document>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await GetAsync(client, path + "/collections/" + collection + "/documents?limit=1000"
                + (cursor is null ? "" : "&cursor=" + System.Uri.EscapeDataString(cursor)), token);
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                documents.Add(item.GetProperty("id").GetString()!, new(item.GetProperty("document").GetRawText(), item.GetProperty("version").GetString()!, 0));
            }

            cursor = page.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);
        return documents;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.GetAsync(Uri(path), token);
        await Docs.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
