using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.E2E.Demo
{
    // Test-only producer protocol. Real dashboard HTTP endpoints, reporting
    // adapters, SDK retention requests and persisted CAS writes remain in use.
    sealed class TestReportingTransport(TestState state, TestWorkspace workspace, Func<string, (string UserId, string Email)?> resolveToken, HttpMessageHandler stripe,
        Func<string, (string UserId, string OrganizationId, string ProjectId, IReadOnlyList<string> Scopes)?> resolveProjectToken, TestAuthApiKeys apiKeys,
        TestDocuments documents) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker vendor = new(stripe, false);
        private readonly ConcurrentDictionary<string, string> scenarios = new();
        private readonly ConcurrentDictionary<string, int> policies = new();
        private readonly ConcurrentQueue<(string UserId, string Path, string Query)> requests = new();
        private readonly ConcurrentQueue<(string UserId, string Path, int Status, string Body)> responses = new();
        public void MapRoutes(WebApplication app)
        {
            app.MapPost("/__test/reporting/scenario", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            {
                var session = await sessions.ReadAsync(http);
                if (session is null)
                {
                    return Results.Unauthorized();
                }

                var context = await accounts.GetContextAsync(session, http.RequestAborted);
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.BadRequest();
                }

                var id = form["project"].ToString(); var scenario = form["scenario"].ToString();
                if (!context.CanManage || context.Organization?.Projects.ContainsKey(id) != true)
                {
                    return Results.StatusCode(403);
                }

                if (scenario is not ("valid" or "missing-latency" or "zero" or "foreign-project" or "foreign-database" or "foreign-region" or "negative" or "overflow" or "meter" or "repeat" or "oversized" or "offline" or "not-metered" or "no-period" or "unauthorized" or "empty" or "received-only"))
                {
                    return Results.BadRequest();
                }

                scenarios[id] = scenario;
                return Results.NoContent();
            });
            app.MapGet("/__test/reporting/probe", async (HttpContext http, DashboardSessions sessions) =>
            {
                var session = await sessions.ReadAsync(http);
                return session is null ? Results.Unauthorized() : Results.Json(new
                {
                    requests = requests.Where(request => request.UserId == session.UserId).Select(request => new { request.Path, request.Query }).ToArray(),
                    responses = responses.Where(response => response.UserId == session.UserId).Select(response => new { response.Path, response.Status, response.Body }).ToArray()
                });
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await SendProducerAsync(request, cancellationToken);
            // An open changes stream is never buffered into the probe log.
            if (request.RequestUri is { Host: "gru.api.terrascale.tech" } uri && response.Content.Headers.ContentType?.MediaType != "text/event-stream" &&
                request.Headers.Authorization?.Parameter is { } token && ResolveUser(token) is { } actor)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                responses.Enqueue((actor.UserId, uri.AbsolutePath, (int)response.StatusCode, body[..Math.Min(body.Length, 4096)]));
            }
            return response;
        }

        private (string UserId, string Email)? ResolveUser(string token)
        {
            if (resolveToken(token) is { } session)
            {
                return session;
            }

            return resolveProjectToken(token) is { } project ? (project.UserId, "") : null;
        }

        private async Task<HttpResponseMessage> SendProducerAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            var changesPath = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["v1", "projects", _, "databases", _, "changes"];
            if (uri.Host == "api.stripe.com")
            {
                return await vendor.SendAsync(request, cancellationToken);
            }

            if (uri.Host == "auth.terrascale.tech")
            {
                return await apiKeys.SendAsync(request, cancellationToken);
            }

            if (!(uri.Host is "api.terrascale.tech" or "gru.api.terrascale.tech" || uri.Host == "sjc.api.terrascale.tech" && changesPath) || request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } token })
            {
                return Json(new { error = "unauthorized" }, HttpStatusCode.Unauthorized);
            }

            var sessionActor = resolveToken(token);
            var scopedActor = resolveProjectToken(token);
            if (sessionActor is null && scopedActor is null)
            {
                return Json(new { error = "unauthorized" }, HttpStatusCode.Unauthorized);
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            var projectIndex = Array.IndexOf(segments, "projects");
            var organizationIndex = Array.IndexOf(segments, "organizations");
            var id = projectIndex >= 0 && segments.Length > projectIndex + 1 ? segments[projectIndex + 1] : "";
            var orgId = organizationIndex >= 0 && segments.Length > organizationIndex + 1 ? segments[organizationIndex + 1] : "";
            OrganizationRecord? org;
            ProjectRecord? project = null;
            lock (workspace.Gate)
            {
                var live = id.Length > 0
                    ? workspace.Organizations.Values.FirstOrDefault(item => item.Projects.ContainsKey(id))
                    : workspace.Organizations.GetValueOrDefault(orgId);
                org = live is null ? null : TestWorkspace.Clone(live);
            }
            if (id.Length > 0)
            {
                project = org?.Projects.GetValueOrDefault(id);
                if (org is null || project?.OrganizationId != org.Id || orgId.Length > 0 && orgId != org.Id)
                {
                    return Json(new { error = "not_found" }, HttpStatusCode.NotFound);
                }
                // Auth owns identity and membership; dashboard storage owns the database references used by reporting fixtures.
                var settings = state.Items.GetValueOrDefault(AccountService.OrganizationsCollection + "/" + org.Id)?.Document.Deserialize<OrganizationRecord>();
                if (settings?.Projects.TryGetValue(id, out var storedProject) == true)
                {
                    project.Databases = storedProject.Databases;
                }
            }
            // TerraBase canonical usage (I2 GetUsageAsync) reads with an exchanged databases:read project token.
            var isCanonicalUsage = segments is ["v1", "projects", _, "usage"] or ["v1", "projects", _, "databases", _, "usage"];
            if (isCanonicalUsage && scopedActor?.Scopes.Contains("databases:read") != true)
            {
                return Json(new { error = new { code = "forbidden_scope", message = "databases:read is required" } }, HttpStatusCode.Forbidden);
            }
            // TerraBase document changes stream with an exchanged documents:read project token for the path's project.
            var isDocumentChanges = segments is ["v1", "projects", _, "databases", _, "changes"];
            if (isDocumentChanges && sessionActor is not null)
            {
                return Json(new { error = "unauthorized" }, HttpStatusCode.Unauthorized);
            }

            if (isDocumentChanges && scopedActor?.Scopes.Contains("documents:read") != true)
            {
                return Json(new { error = new { code = "forbidden_scope", message = "documents:read is required" } }, HttpStatusCode.Forbidden);
            }

            var isDeclaredSchema = request.Method == HttpMethod.Get && segments is ["v1", "projects", _, "databases", _, "collections", _, "schema"];
            if (isDeclaredSchema && scopedActor?.Scopes.Contains("documents:read") != true)
            {
                return Json(new { error = new { code = "forbidden_scope", message = "documents:read is required" } }, HttpStatusCode.Forbidden);
            }

            var isScopedReporting = segments.Last() is "analytics" or "replication" || isCanonicalUsage || isDocumentChanges || isDeclaredSchema;
            var actorUserId = sessionActor?.UserId ?? scopedActor!.Value.UserId;
            if (isScopedReporting && (sessionActor is not null || scopedActor is null || scopedActor.Value.ProjectId != id || scopedActor.Value.OrganizationId != org?.Id) ||
                !isScopedReporting && sessionActor is null)
            {
                return Json(new { error = "unauthorized" }, HttpStatusCode.Unauthorized);
            }

            if (org is null || !org.ContainsMember(actorUserId) || workspace.ScopedRole(actorUserId, org.Id).Length == 0)
            {
                return Json(new { error = "forbidden" }, HttpStatusCode.Forbidden);
            }

            if (request.Headers.TryGetValues("X-TerraBase-User-Id", out var users) && users.Single() != actorUserId ||
                request.Headers.TryGetValues("X-TerraBase-Organization-Id", out var organizations) && organizations.Single() != org.Id)
            {
                return Json(new { error = "forbidden" }, HttpStatusCode.Forbidden);
            }

            requests.Enqueue((actorUserId, uri.AbsolutePath, uri.Query));
            if (segments.Last() == "audit-retention")
            {
                if (workspace.ScopedRole(actorUserId, org.Id) is not ("owner" or "admin"))
                {
                    return Json(new { error = "forbidden" }, HttpStatusCode.Forbidden);
                }

                if (request.Method == HttpMethod.Put)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var days = body.RootElement.GetProperty("days").GetInt32();
                    if (days is < 1 or > 90)
                    {
                        return Json(new { error = "invalid_retention" }, HttpStatusCode.BadRequest);
                    }

                    policies[org.Id] = days;
                }
                return Json(new { days = policies.GetValueOrDefault(org.Id, 90) });
            }
            if (project is null || request.Method != HttpMethod.Get)
            {
                throw new HttpRequestException("Unknown reporting fixture request.");
            }

            if (isDeclaredSchema)
            {
                var scope = new DatabaseScope(actorUserId, "", org.Id, org.Name, project.Id, project.Name);
                if (!project.Databases.Values.Any(database => database.Name == segments[4]) ||
                    !documents.Statistics(scope, segments[4]).Any(collection => collection.Name == segments[6]))
                {
                    return Json(new { error = new { code = "not_found", message = "collection not found" } }, HttpStatusCode.NotFound);
                }
                // This fixture has no declared validation schemas; stateful mutations use DatabaseSdkScenario.
                return Json(new { schema = (object?)null, dialect = (string?)null });
            }
            if (isDocumentChanges)
            {
                return project.Databases.Values.Any(database => database.Name == segments[4])
                    ? documents.Changes(project.Id, segments[4], uri.Host == "api.terrascale.tech" ? null : uri.Host.Split('.')[0], request)
                    : Json(new { error = new { code = "not_found", message = "database not found" } }, HttpStatusCode.NotFound);
            }

            var scenario = scenarios.GetValueOrDefault(project.Id, "valid");
            if (scenario == "offline")
            {
                return Json(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable);
            }

            if (scenario == "not-metered")
            {
                return Json(new { error = "not_metered" }, HttpStatusCode.NotFound);
            }

            if (scenario == "unauthorized")
            {
                return Json(new { error = "unauthorized" }, HttpStatusCode.Unauthorized);
            }

            if (scenario == "oversized")
            {
                return new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) };
            }

            var query = QueryHelpers.ParseQuery(uri.Query);
            if (isCanonicalUsage)
            {
                return CanonicalUsage(project, segments, query, scenario);
            }

            if (segments.Last() == "usage")
            {
                return Usage(project, segments, query, scenario);
            }

            if (segments.Last() is not ("analytics" or "replication"))
            {
                throw new HttpRequestException("Unknown reporting fixture contract.");
            }

            var databaseId = query.GetValueOrDefault("database").ToString();
            var regionId = query.GetValueOrDefault("region").ToString();
            var selected = project.Databases.Values.Where(database => databaseId.Length == 0 || database.Id == databaseId).ToArray();
            if (databaseId.Length > 0 && selected.Length == 0)
            {
                return Json(new { error = "not_found" }, HttpStatusCode.NotFound);
            }

            var analyticsRange = query.GetValueOrDefault("range").ToString() is { Length: > 0 } requestedRange ? requestedRange : "24h";
            var bucketSeconds = analyticsRange switch { "1h" => 60, "24h" => 3600, "7d" => 21600, "30d" => 86400, _ => 3600 };
            var durationSeconds = analyticsRange switch { "1h" => 3600, "24h" => 86400, "7d" => 604800, "30d" => 2592000, _ => 86400 };
            var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var end = DateTimeOffset.FromUnixTimeSeconds((nowSeconds / bucketSeconds + 1) * bucketSeconds);
            var first = end.AddSeconds(-durationSeconds);
            var rows = new List<object>();
            foreach (var database in selected)
            {
                var code = regionId.Length > 0 ? regionId switch { "gru" => "GRU", "sjc" => "SJC", _ => regionId } : database.Region;
                if (!database.Regions.Prepend(database.Region).Contains(code))
                {
                    return Json(new { error = "not_found" }, HttpStatusCode.NotFound);
                }

                if (segments.Last() == "analytics")
                {
                    rows.Add(new
                    {
                        databaseId = scenario == "foreign-database" ? "foreign" : database.Id,
                        region = scenario == "foreign-region" ? "??" : RegionId(code),
                        databaseName = database.Name,
                        series = new[]
                        {
                            new
                            {
                                start = first,
                                requests = new Dictionary<string, long> { ["GET"] = scenario == "negative" ? -3 : scenario == "zero" ? 0 : 10 },
                                operations = new Dictionary<string, long> { ["document_read"] = scenario == "zero" ? 0 : 4 },
                                errors = scenario == "zero" ? 0 : 1,
                                latency = new { p50Ms = scenario == "zero" || scenario == "missing-latency" ? (double?)null : 0.5, p95Ms = scenario == "zero" || scenario == "missing-latency" ? (double?)null : 1.75, p99Ms = scenario == "zero" || scenario == "missing-latency" ? (double?)null : 9d },
                                storageBytes = scenario == "missing-latency" ? (long?)null : 4096,
                                clientBandwidth = new { bytesSent = 6L, bytesReceived = 7L },
                                replicationBandwidth = new { bytesSent = scenario == "received-only" ? 0L : 8L, bytesReceived = 9L }
                            }
                        }
                    });
                }
                else
                {
                    rows.Add(new
                    {
                        databaseId = scenario == "foreign-database" ? "foreign" : database.Id,
                        region = scenario == "foreign-region" ? "??" : RegionId(code),
                        databaseName = database.Name,
                        status = "healthy",
                        replicatedOps = 12,
                        lagMs = (double?)0,
                        divergence = (bool?)null,
                        convergenceMs = (double?)null,
                        lastApplied = (DateTimeOffset?)null,
                        bandwidth = database.Regions.Where(value => value != code).Select(destination => new { sourceRegion = RegionId(code), destinationRegion = RegionId(destination), kind = "replication", bytesSent = 8L, bytesReceived = 9L }).ToArray()
                    });
                }
            }
            if (scenario == "empty")
            {
                rows.Clear();
            }

            var providerId = scenario == "foreign-project" ? "foreign-project" : project.Id;
            return segments.Last() == "analytics" ? Json(new { projectId = providerId, range = analyticsRange, from = first, to = end, bucketSeconds, items = rows })
                : Json(new { projectId = providerId, range = analyticsRange, from = first, to = end, bucketSeconds, items = rows });
        }
        private static HttpResponseMessage Usage(ProjectRecord project, string[] segments, Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query, string scenario)
        {
            var databaseIndex = Array.IndexOf(segments, "databases");
            var databaseId = databaseIndex < 0 ? "" : segments[databaseIndex + 1];
            if (databaseId.Length > 0 && !project.Databases.ContainsKey(databaseId))
            {
                return Json(new { error = "not_found" }, HttpStatusCode.NotFound);
            }

            var from = DateTimeOffset.Parse(query["from"].ToString(), CultureInfo.InvariantCulture).ToUniversalTime();
            var granularity = query["granularity"].ToString();
            var now = DateTimeOffset.UtcNow;
            var buckets = Math.Max(1, (int)Math.Ceiling((now - from).TotalHours / (granularity == "day" ? 24 : 1)));
            var rows = project.Databases.Values.Where(database => databaseId.Length == 0 || database.Id == databaseId).ToArray();
            var factor = rows.Length;
            var reads = scenario == "negative" ? -1 : scenario == "overflow" ? long.MaxValue : scenario == "zero" ? 0 : 12;
            object Reading(string id, int count, bool child) => new
            {
                projectId = scenario == "foreign-project" ? "foreign-project" : project.Id,
                databaseId = scenario == "foreign-database" ? "foreign" : id,
                logicalBytes = 4096L * count,
                storageBytes = 8192L * count,
                documents = 2L * count,
                reads = 999_999L,
                writes = 888_888L,
                period = scenario == "no-period" ? null : new { from, to = now, granularity, reads = reads == long.MaxValue ? reads : reads * count, writes = 7L * count },
                series = child ? Array.Empty<object>() : Enumerable.Range(0, buckets).Select(index => (object)new { start = granularity == "day" ? from.AddDays(index) : from.AddHours(index), reads = index == 0 ? reads == long.MaxValue ? reads : reads * count : 0, writes = index == 0 ? 7L * count : 0 }).ToArray(),
                items = child || databaseId.Length > 0 ? Array.Empty<object>() : rows.Select(database => Reading(database.Id, 1, true)).ToArray()
            };
            return Json(Reading(databaseId, factor, false));
        }
        // I2 contracts/terrabase project-usage.json and database-usage.json: totals equal their items, period equals its series,
        // {database} is the name and from/to are rounded to UTC buckets.
        private static HttpResponseMessage CanonicalUsage(ProjectRecord project, string[] segments, Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query, string scenario)
        {
            var databaseName = segments.Length == 6 ? segments[4] : "";
            var selected = project.Databases.Values.Where(database => databaseName.Length == 0 || database.Name == databaseName).Select(database => database.Id).ToArray();
            if (databaseName.Length > 0 && selected.Length != 1)
            {
                return Json(new { error = new { code = "not_found", message = "database not found" } }, HttpStatusCode.NotFound);
            }

            var granularity = query.GetValueOrDefault("granularity").ToString() is { Length: > 0 } requested ? requested : "hour";
            if (granularity is not ("hour" or "day") || !query.ContainsKey("from"))
            {
                return Json(new { error = new { code = "invalid_query", message = "granularity is hour or day; from is required here" } }, HttpStatusCode.BadRequest);
            }

            var step = granularity == "day" ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour;
            var from = new DateTimeOffset(DateTimeOffset.Parse(query["from"].ToString(), CultureInfo.InvariantCulture).UtcTicks / step * step, TimeSpan.Zero);
            // As TerraBase's UsageWindow: to defaults to now.
            var end = query.ContainsKey("to") ? DateTimeOffset.Parse(query["to"].ToString(), CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
            var to = new DateTimeOffset((end.UtcTicks + step - 1) / step * step, TimeSpan.Zero);
            var buckets = (int)((to - from).Ticks / step);
            var reads = scenario == "negative" ? -1 : scenario == "overflow" ? long.MaxValue : scenario == "zero" ? 0 : 12;
            var writes = scenario == "zero" ? 0L : 7L;
            object? Period(long r, long w) => scenario == "no-period" ? null : new { from, to, granularity, reads = r, writes = w };
            object[] Series(long r, long w) => Enumerable.Range(0, buckets).Select(index => (object)new { start = from.AddTicks(step * index), reads = index == 0 ? r : 0, writes = index == 0 ? w : 0 }).ToArray();
            object Database(string id, bool child) => new
            {
                databaseId = scenario == "foreign-database" ? "foreign" : id,
                storageBytes = 8192L,
                logicalBytes = 4096L,
                collections = 1L,
                documents = 2L,
                reads,
                writes,
                period = Period(reads, writes),
                series = child ? Array.Empty<object>() : Series(reads, writes)
            };
            if (databaseName.Length > 0)
            {
                return Json(Database(selected[0], false));
            }

            var count = selected.Length;
            var total = unchecked(reads * count);
            return Json(new
            {
                projectId = scenario == "foreign-project" ? "foreign-project" : project.Id,
                databases = count,
                storageBytes = 8192L * count,
                logicalBytes = 4096L * count,
                documents = 2L * count,
                reads = total,
                writes = writes * count,
                period = Period(total, writes * count),
                series = Series(total, writes * count),
                items = selected.Select(id => Database(id, true)).ToArray()
            });
        }
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new(status) { Content = new StringContent(JsonSerializer.Serialize(value), new MediaTypeHeaderValue("application/json")) };
        }

        private static string RegionId(string code)
        {
            return code switch { "GRU" => "gru", "SJC" => "sjc", _ => code };
        }
    }
}
