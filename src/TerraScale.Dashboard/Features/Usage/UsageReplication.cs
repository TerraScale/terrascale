using TerraScale.Dashboard.Platform.Observability;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Usage
{
    public sealed record UsageReplica(string DatabaseId, string DatabaseName, string Region, string Status,
        long ReplicatedOps, double? LagMs, bool? Divergence, double? ConvergenceMs, DateTimeOffset? LastApplied);
    public sealed record UsageReplicationPair(string DatabaseId, string DatabaseName, string FromRegion, string ToRegion,
        long SentBytes, long ReceivedBytes)
    {
        public long WireBytes { get; init; } = ReceivedBytes > 0 ? ReceivedBytes : SentBytes;
    }
    public sealed record UsageReplication(IReadOnlyList<UsageReplica> Regions, IReadOnlyList<UsageReplicationPair> Pairs,
        long Operations = 0, long SentBytes = 0, long ReceivedBytes = 0, double? WorstLagMs = null,
        string Error = "", string Empty = "No replication observations are available for this project.")
    {
        public long WireBytes => Pairs.Aggregate(0L, (sum, pair) => checked(sum + pair.WireBytes));
    }

    public interface IUsageReplication
    {
        Task<UsageReplication> ReadAsync(UsageSelection selection, CancellationToken cancellationToken);
    }

    // TerraBase currently exposes replication as a project-scoped reporting endpoint,
    // not through the pinned database SDK. Keep this adapter aligned to that wire
    // contract and obtain its narrowly scoped token through ts-auth.
    public sealed class UsageReplicationBackend(IHttpClientFactory factory, IProjectAccessTokens projectTokens, IDatabaseBackend databases) : IUsageReplication
    {
        public async Task<UsageReplication> ReadAsync(UsageSelection selection, CancellationToken cancellationToken)
        {
            var context = selection.Context;
            var organization = context.Organization;
            var project = context.Project;
            if (organization is null || project is null || project.OrganizationId != organization.Id ||
                !organization.Projects.TryGetValue(project.Id, out var canonicalProject) || canonicalProject.Id != project.Id || canonicalProject.OrganizationId != organization.Id)
            {
                return new([], []);
            }

            project = canonicalProject;
            // TerraBase registers a project with its first database, so an empty project has nothing to report (and is 404 there).
            if (project.Databases.Count == 0)
            {
                return new([], [], Empty: "No activity yet. Replication starts once this project has a database.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var client = factory.CreateClient("platform");
                var origin = await new TerraBaseDiscovery().ResolveAsync(deadline.Token);
                var token = await projectTokens.GetAsync(context.Session, organization.Id, project.Id, ["databases:read"], deadline.Token);
                var path = $"/v1/projects/{Uri.EscapeDataString(project.Id)}/replication?range={Uri.EscapeDataString(selection.Range)}";
                if (selection.DatabaseId.Length > 0)
                {
                    path += "&database=" + Uri.EscapeDataString(selection.DatabaseId);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new([], [], Error: "TerraBase couldn't find this project for replication reporting.");
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return new([], [], Error: "TerraBase didn't authorize replication data for this project.");
                }

                response.EnsureSuccessStatusCode();
                using var json = await PlatformJson.ReadAsync(response.Content, deadline.Token);
                var regionCatalog = await databases.RegionsAsync(deadline.Token);
                return Parse(json.RootElement, project, selection.DatabaseId, selection.Range, regionCatalog);
            }
            catch (Exception error) when (Unavailable(error, cancellationToken))
            {
                ServiceSloTelemetry.MarkFailure(error);
                return new([], [], Error: "Replication data could not be loaded from TerraBase. Try again shortly.");
            }
        }

        private static UsageReplication Parse(JsonElement root, ProjectRecord project, string selectedDatabaseId, string requestedRange,
            IReadOnlyList<DatabaseRegion> regionCatalog)
        {
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("projectId").GetString() != project.Id ||
                root.GetProperty("range").GetString() != requestedRange)
            {
                throw new JsonException("Replication response has a different project or range.");
            }

            var from = root.GetProperty("from").GetDateTimeOffset();
            var to = root.GetProperty("to").GetDateTimeOffset();
            var bucketSeconds = Positive(root.GetProperty("bucketSeconds"));
            var (expectedBucketSeconds, expectedDuration) = requestedRange switch
            {
                "1h" => (60, TimeSpan.FromHours(1)),
                "24h" => (3600, TimeSpan.FromHours(24)),
                "7d" => (21600, TimeSpan.FromDays(7)),
                "30d" => (86400, TimeSpan.FromDays(30)),
                _ => throw new JsonException("Replication response used an unsupported range.")
            };
            if (from >= to || from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || bucketSeconds != expectedBucketSeconds ||
                to - from != expectedDuration || from.ToUnixTimeSeconds() % bucketSeconds != 0 || to.ToUnixTimeSeconds() % bucketSeconds != 0)
            {
                throw new JsonException("Replication response has an invalid time range.");
            }

            var rows = root.GetProperty("items");
            if (rows.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Replication items must be an array.");
            }

            var health = new List<UsageReplica>();
            var pairTotals = new Dictionary<(string DatabaseId, string From, string To, string Kind), (long Sent, long Received)>();
            long operations = 0, sentBytes = 0, receivedBytes = 0;
            double? worstLag = null;
            var seen = new HashSet<(string DatabaseId, string Region)>();
            foreach (var row in rows.EnumerateArray())
            {
                var databaseId = RequiredString(row, "databaseId");
                if (selectedDatabaseId.Length > 0 && databaseId != selectedDatabaseId ||
                    !project.Databases.TryGetValue(databaseId, out var database) || database.ProjectId != project.Id)
                {
                    throw new JsonException("Replication response includes a database outside the selected project scope.");
                }

                var databaseName = RequiredString(row, "databaseName");
                if (databaseName != database.Name)
                {
                    throw new JsonException("Replication database identity does not match the selected project.");
                }

                var producerRegion = RequiredString(row, "region");
                if (!ValidRegion(producerRegion, regionCatalog))
                {
                    throw new JsonException("Replication response includes an invalid region identifier.");
                }

                var region = DisplayRegion(producerRegion, regionCatalog);
                if (!seen.Add((databaseId, region)))
                {
                    throw new JsonException("Replication response repeats a database region.");
                }

                var status = RequiredString(row, "status");
                var replicatedOps = Nonnegative(row.GetProperty("replicatedOps"));
                var lag = OptionalNonnegativeDouble(row, "lagMs");
                var divergence = OptionalBoolean(row, "divergence");
                var convergence = OptionalNonnegativeDouble(row, "convergenceMs");
                var lastApplied = OptionalTimestamp(row, "lastApplied");
                operations = checked(operations + replicatedOps);
                if (lag is not null)
                {
                    worstLag = Math.Max(worstLag ?? 0, lag.Value);
                }

                health.Add(new(databaseId, databaseName, region, status, replicatedOps, lag, divergence, convergence, lastApplied));

                var bandwidth = row.GetProperty("bandwidth");
                if (bandwidth.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("Replication bandwidth must be an array.");
                }

                foreach (var meter in bandwidth.EnumerateArray())
                {
                    var producerSource = RequiredString(meter, "sourceRegion");
                    var producerDestination = RequiredString(meter, "destinationRegion");
                    var kind = RequiredString(meter, "kind");
                    if (producerSource == producerDestination || !ValidRegion(producerSource, regionCatalog) || !ValidRegion(producerDestination, regionCatalog))
                    {
                        throw new JsonException("Replication bandwidth includes an invalid region pair.");
                    }

                    var source = DisplayRegion(producerSource, regionCatalog);
                    var destination = DisplayRegion(producerDestination, regionCatalog);
                    if (source == destination)
                    {
                        throw new JsonException("Replication bandwidth includes a self-replication pair.");
                    }

                    var sent = Nonnegative(meter.GetProperty("bytesSent"));
                    var received = Nonnegative(meter.GetProperty("bytesReceived"));
                    sentBytes = checked(sentBytes + sent);
                    receivedBytes = checked(receivedBytes + received);
                    var key = (databaseId, source, destination, kind);
                    var (Sent, Received) = pairTotals.GetValueOrDefault(key);
                    pairTotals[key] = (checked(Sent + sent), checked(Received + received));
                }
            }

            // Both endpoints can observe the same transfer. Prefer received bytes, else sent, per directed pair AND kind
            // before combining kinds/databases. Never add sent + received as wire traffic (also supports receiver-only producers).
            var pairs = pairTotals.GroupBy(item => (item.Key.DatabaseId, item.Key.From, item.Key.To))
                .Select(group => new UsageReplicationPair(group.Key.DatabaseId, project.Databases[group.Key.DatabaseId].Name,
                    group.Key.From, group.Key.To, group.Sum(item => item.Value.Sent), group.Sum(item => item.Value.Received))
                { WireBytes = group.Sum(item => item.Value.Received > 0 ? item.Value.Received : item.Value.Sent) })
                .OrderBy(item => item.DatabaseId, StringComparer.Ordinal)
                .ThenBy(item => item.FromRegion, StringComparer.Ordinal).ThenBy(item => item.ToRegion, StringComparer.Ordinal).ToArray();
            return new(health, pairs, operations, sentBytes, receivedBytes, worstLag);
        }

        private static string DisplayRegion(string producerRegion, IReadOnlyList<DatabaseRegion> regionCatalog)
        {
            return (regionCatalog.FirstOrDefault(region => region.Id == producerRegion) ?? DatabaseRegion.FromId(producerRegion))?.Code ?? producerRegion;
        }

        private static bool ValidRegion(string value, IReadOnlyList<DatabaseRegion> regionCatalog)
        {
            return regionCatalog.Any(region => region.Id == value || region.Code == value) ||
            value.Length is > 0 and <= 63 && value[0] is >= 'a' and <= 'z' && value[^1] != '-' &&
            value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
        }

        private static string RequiredString(JsonElement obj, string name)
        {
            var value = obj.GetProperty(name);
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new JsonException("Replication response has a missing string field.");
            }

            return value.GetString()!;
        }

        private static long Nonnegative(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0
            ? count : throw new JsonException("Replication counters must be nonnegative integers.");
        }

        private static long Positive(JsonElement value)
        {
            var count = Nonnegative(value);
            return count > 0 ? count : throw new JsonException("Replication bucket size must be positive.");
        }
        private static double? OptionalNonnegativeDouble(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0)
            {
                throw new JsonException("Replication measurements must be finite and nonnegative.");
            }

            return number;
        }
        private static bool? OptionalBoolean(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new JsonException("Replication divergence must be a boolean.") };
        }
        private static DateTimeOffset? OptionalTimestamp(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var timestamp) || timestamp.Offset != TimeSpan.Zero)
            {
                throw new JsonException("Replication timestamps must be UTC.");
            }

            return timestamp;
        }
        private static bool Unavailable(Exception error, CancellationToken cancellationToken)
        {
            return error is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }
    }
}
