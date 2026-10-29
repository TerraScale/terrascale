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
            string[] cuts = ["manual-after-commit", "pitr-checkpoint", "pitr-latest", "before-window"];
            var source = await GetAsync(client, cluster.DatabasePath, token);
            var organization = cluster.OrganizationId ?? throw new InvalidOperationException("backup qualification needs the source organization id");
            var sourceId = source.GetProperty("id").GetString()!;
            string[] placement = [source.GetProperty("homeRegion").GetString()!,
                .. source.GetProperty("replicaRegions").EnumerateArray().Select(region => region.GetString()!)];
            var projectId = cluster.ProjectPath.Split('/')[^1];
            var acknowledged = new Dictionary<string, string>(StringComparer.Ordinal);
            var acknowledgedVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            var fake = new Faker();
            foreach (var region in Cluster.Regions)
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
                new(cuts[3], false, first, "backup_history_incomplete", 409),
            ]);
            foreach (var cut in plan)
            {
                var name = "e2e-restore-" + EntityIds.New().ToString().ToLowerInvariant();
                var path = cluster.ProjectPath + "/databases/" + name;
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

                using var restored = await client.PostAsJsonAsync(Uri(route), new { name, pointInTime = at }, token);
                var holder = client;
                if (restored.StatusCode != HttpStatusCode.Created)
                {
                    var refusal = await restored.Content.ReadFromJsonAsync<JsonElement>(token);
                    var code = refusal.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var value) ? value.GetString() : null;
                    outcomes.Add(new(cut.Name, null, code, (int)restored.StatusCode));
                }
                else if (!cut.Supported)
                {
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
                    nodes = Cluster.Regions.Select(Cluster.Identity).ToArray(),
                }));
            }

            result = new(plan, outcomes, [.. Cluster.Regions.Select(Cluster.Identity)], points);
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
                backup = new Run(plan, outcomes, [.. Cluster.Regions.Select(Cluster.Identity)], points),
                errors = errors.Select(error => error.Message).ToArray(),
            }));
        }

        return errors.Count == 0 ? result! : throw new AggregateException("backup qualification or cleanup failed", errors);
    }

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
        var dates = await Task.WhenAll(Cluster.Regions.Select(async region =>
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
        if (transaction.GetProperty("durability").GetString() != "local_durable")
        {
            throw new InvalidOperationException("transaction was not acknowledged as local_durable");
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

    private static async Task<JsonElement> ShippedAsync(Cluster cluster, DateTimeOffset from, CancellationToken token)
    {
        _ = await Converge.UntilAsync(() => ServedAtAsync(cluster[Cluster.Gru], cluster.DatabasePath, token),
            date => date >= from, "the backup checkpoint boundary", token);
        return await Converge.UntilAsync(async () =>
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
    }

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
