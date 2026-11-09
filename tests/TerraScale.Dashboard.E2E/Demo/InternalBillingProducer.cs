using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.E2E
{
    sealed class InternalBillingProducer(TestWorkspace workspace, Func<Task<DashboardDbContext>> storage, Func<DateTimeOffset> now, Func<string> subject,
        Action<OrganizationRecord>? seedOrganization = null)
    {
        private readonly OrganizationStore organizations = new(new TestStorageFactory(storage));
        private readonly AccountStore accounts = new(new TestStorageFactory(storage), TimeProvider.System);
        private readonly ConcurrentQueue<object> usageRequests = new();
        private readonly ConcurrentQueue<object> scopeRequests = new();
        private readonly ConcurrentQueue<string> stripeRequests = new();
        private string usageMode = "coherent";
        private TaskCompletionSource? usageGate;
        private int pausedUsage;
        private int failCliBuild;
        private int cliBuildFailures;

        public async Task<IResult> ControlAsync(JsonElement command, CancellationToken cancellationToken)
        {
            switch (command.GetProperty("command").GetString())
            {
                case "internal-seed":
                    return await SeedAsync(command.TryGetProperty("count", out var count) ? count.GetInt32() : 1,
                    command.TryGetProperty("paymentMethodValidated", out var validation) && validation.GetBoolean(), cancellationToken);
                case "internal-add-database": return await AddDatabaseAsync(command.TryGetProperty("name", out var databaseName) ? databaseName.GetString() ?? "app" : "app", cancellationToken);
                case "internal-usage":
                    var mode = command.GetProperty("value").GetString();
                    if (mode is not ("coherent" or "partial" or "foreign-project" or "wrong-month" or "incoherent-end" or "overflow" or "offline"))
                    {
                        return Results.BadRequest();
                    }

                    usageMode = mode;
                    break;
                case "internal-pause-usage":
                    pausedUsage = 0;
                    usageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "internal-release-usage": usageGate?.TrySetResult(); usageGate = null; break;
                case "internal-fail-cli-build-once": Interlocked.Exchange(ref failCliBuild, 1); break;
                default: return Results.BadRequest();
            }
            return Results.Ok(new { configured = true });
        }

        public void Release()
        {
            usageGate?.TrySetResult();
            usageGate = null;
        }

        private async Task<IResult> AddDatabaseAsync(string name, CancellationToken cancellationToken)
        {
            if (await accounts.FindAsync(subject(), cancellationToken) is not { Erased: false } accountRow)
            {
                return Results.BadRequest();
            }

            var account = accountRow.Account;
            if (await organizations.FindAsync(account.ActiveOrganizationId, cancellationToken) is not { Erased: false } organizationRow)
            {
                return Results.BadRequest();
            }

            var organization = organizationRow.Organization;
            var project = organization.Projects[account.ActiveProjectId];
            var database = new DatabaseRecord { Id = new TerraScaleIdGenerator().NewId(), ProjectId = project.Id, Name = name, Region = "GRU", Regions = ["GRU", "FRA", "SJC"], CreatedAt = now() };
            project.Databases[database.Id] = database;
            await organizations.ReplaceAsync(organizationRow, cancellationToken);
            return Results.NoContent();
        }

        private async Task<IResult> SeedAsync(int count, bool paymentMethodValidated, CancellationToken cancellationToken)
        {
            if (count is < 1 or > 2)
            {
                return Results.BadRequest();
            }

            var owner = subject();
            if (await accounts.FindAsync(owner, cancellationToken) is not { Erased: false } accountRow)
            {
                return Results.BadRequest(new { error = "Sign in natively before preparing the pre-existing provider target." });
            }

            var account = accountRow.Account;
            var ids = new TerraScaleIdGenerator();
            var targets = new List<object>();
            for (var index = 0; index < count; index++)
            {
                var organization = new OrganizationRecord
                {
                    Id = ids.NewId(),
                    Name = "terrascale",
                    Kind = "business",
                    Slug = "terrascale",
                    OwnerUserId = owner,
                    PaymentMethodValidated = paymentMethodValidated,
                    CreatedAt = now()
                };
                organization.Members[owner] = new() { UserId = owner, Name = account.Name, Email = account.Email, Role = "owner", CreatedAt = now() };
                foreach (var name in new[] { "ts-dashboard", "ts-auth", "ts-router", "ts-status", "ts-orchestrator", "ku" })
                {
                    var project = new ProjectRecord { Id = ids.NewId(), OrganizationId = organization.Id, Name = name, Slug = name, Region = "GRU", Regions = ["GRU"], CreatedAt = now() };
                    var database = new DatabaseRecord { Id = ids.NewId(), ProjectId = project.Id, Name = "app", Region = "GRU", Regions = ["GRU"], CreatedAt = now() };
                    project.Databases[database.Id] = database;
                    organization.Projects[project.Id] = project;
                }
                lock (workspace.Gate)
                {
                    workspace.Organizations[organization.Id] = TestWorkspace.Clone(organization);
                }

                await organizations.CreateAsync(organization, cancellationToken);
                seedOrganization?.Invoke(organization);
                account.OrganizationIds.Add(organization.Id);
                if (index == 0) { account.ActiveOrganizationId = organization.Id; account.ActiveProjectId = organization.Projects.Keys.First(); }
                targets.Add(new { organizationId = organization.Id, projects = organization.Projects.Values.Select(project => new { project.Id, project.Name }).ToArray() });
            }
            await accounts.ReplaceAsync(accountRow, cancellationToken);
            return Results.Json(new { targets });
        }

        // TerraBase GET /v1/projects/{project}/usage (contracts/terrabase/fixtures/project-usage.json). The dispatcher has already
        // validated the exchanged project bearer and supplies its principal, organization and scopes.
        public async Task<IResult> UsageAsync(HttpContext http, string principal, string organizationId, string projectId, IReadOnlyList<string> scopes)
        {
            if (http.Request.Method != "GET" || http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries) is not ["v1", "projects", var routed, "usage"] || routed != projectId)
            {
                return Error(HttpStatusCode.NotFound, "not_found", "Only project usage is produced by this fixture.");
            }

            if (!scopes.Contains("databases:read"))
            {
                return Error(HttpStatusCode.Forbidden, "forbidden", "databases:read is required.");
            }
            // ts-auth owns identity, membership and projects; dashboard storage owns the database references.
            OrganizationRecord? live;
            lock (workspace.Gate)
            {
                live = workspace.Organizations.GetValueOrDefault(organizationId) is { } found ? TestWorkspace.Clone(found) : null;
            }

            if (live is null || !live.Projects.TryGetValue(projectId, out var project) || project.OrganizationId != organizationId)
            {
                return Error(HttpStatusCode.NotFound, "not_found", "Project not found.");
            }

            if (workspace.ScopedRole(principal, organizationId).Length == 0)
            {
                return Error(HttpStatusCode.Forbidden, "forbidden", "Not a member of the organization.");
            }

            var settings = (await organizations.FindAsync(organizationId, http.RequestAborted))?.Organization;
            var databases = settings?.Projects.GetValueOrDefault(projectId)?.Databases.Values.Where(database => database.ProjectId == projectId).OrderBy(database => database.CreatedAt).ToArray() ?? [];
            if (!Window(http.Request.Query, now(), out var granularity, out var from, out var to))
            {
                return Error(HttpStatusCode.BadRequest, "invalid_request", "from and to must be RFC 3339 instants; granularity must be hour or day.");
            }

            (granularity == "day" ? usageRequests : scopeRequests).Enqueue(new { organizationId, projectId, userId = principal, granularity, from, to });
            if (usageGate is { } gate)
            {
                Interlocked.Increment(ref pausedUsage);
                await gate.Task.WaitAsync(http.RequestAborted);
            }
            var ordinal = live.Projects.Keys.ToList().IndexOf(projectId);
            if (usageMode == "offline")
            {
                return Error(HttpStatusCode.ServiceUnavailable, "unavailable", "Usage is unavailable.");
            }

            if (usageMode == "partial" && ordinal == 0)
            {
                return Error(HttpStatusCode.NotFound, "not_found", "Project usage not found.");
            }

            var period = new
            {
                from = usageMode == "wrong-month" ? from.AddMonths(-1) : from,
                to = usageMode == "incoherent-end" && ordinal == 0 ? to.AddSeconds(1) : to,
                granularity
            };
            // All metered counts sit in the first database's first bucket of the window, so series, items and totals agree.
            var items = databases.Select((database, index) =>
            {
                long reads = ordinal == 0 && index == 0 ? 123_456 : 0;
                long writes = usageMode == "overflow" && ordinal < 2 && index == 0 ? long.MaxValue : ordinal == 0 && index == 0 ? 106_000_000 : 0;
                long bytes = ordinal == 0 && index == 0 ? 12_000_000_000L : 0;
                return new
                {
                    databaseId = database.Id,
                    storageBytes = bytes,
                    logicalBytes = bytes,
                    tables = bytes > 0 ? 1 : 0,
                    rows = bytes > 0 ? 17 : 0,
                    reads,
                    writes,
                    period = new { period.from, period.to, period.granularity, reads, writes },
                    series = Array.Empty<object>()
                };
            }).ToArray();
            long totalReads = items.Sum(item => item.reads), totalWrites = items.Sum(item => item.writes), totalBytes = items.Sum(item => item.logicalBytes);
            var step = granularity == "day" ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour;
            return Results.Json(new
            {
                projectId = usageMode == "foreign-project" ? "foreign-project" : projectId,
                databases = items.Length,
                storageBytes = totalBytes,
                logicalBytes = totalBytes,
                rows = items.Sum(item => item.rows),
                reads = totalReads,
                writes = totalWrites,
                period = new { period.from, period.to, period.granularity, reads = totalReads, writes = totalWrites },
                series = Enumerable.Range(0, (int)((to - from).Ticks / step))
                    .Select(index => new { start = from.AddTicks(index * step), reads = index == 0 ? totalReads : 0, writes = index == 0 ? totalWrites : 0 }).ToArray(),
                items
            });
        }

        private static IResult Error(HttpStatusCode status, string code, string message)
        {
            return Results.Json(new { error = new { code, message } }, statusCode: (int)status);
        }

        // TerraBase UsageWindow: UTC hour or day buckets, from rounded down, to (default now) rounded up, at most 840 hours or 400 days.
        private static bool Window(IQueryCollection query, DateTimeOffset current, out string granularity, out DateTimeOffset from, out DateTimeOffset to)
        {
            string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
            granularity = query["granularity"].ToString() is "" or "hour" ? "hour" : query["granularity"].ToString();
            from = to = default;
            if (granularity is not ("hour" or "day"))
            {
                return false;
            }

            DateTimeOffset requestedTo = current, requestedFrom = default;
            if (query.TryGetValue("to", out var rawTo) && !DateTimeOffset.TryParseExact(rawTo, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out requestedTo))
            {
                return false;
            }

            if (query.TryGetValue("from", out var rawFrom) && (!DateTimeOffset.TryParseExact(rawFrom, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out requestedFrom) || requestedFrom >= requestedTo))
            {
                return false;
            }

            var ticks = granularity == "day" ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour;
            var end = (requestedTo.UtcTicks + ticks - 1) / ticks;
            var first = query.ContainsKey("from") ? requestedFrom.UtcTicks / ticks : end - (granularity == "hour" ? 24 : 30);
            if (end - first < 1 || end - first > (granularity == "hour" ? 840 : 400))
            {
                return false;
            }

            from = new DateTimeOffset(first * ticks, TimeSpan.Zero);
            to = new DateTimeOffset(end * ticks, TimeSpan.Zero);
            return true;
        }

        public void ObserveStripe(HttpRequestMessage request)
        {
            stripeRequests.Enqueue(request.Method + " " + request.RequestUri!.AbsolutePath);
        }

        public void BeforeCliBuild()
        {
            if (Interlocked.Exchange(ref failCliBuild, 0) == 1)
            {
                Interlocked.Increment(ref cliBuildFailures);
                throw new IOException("Injected test-only whole CLI application construction failure.");
            }
        }

        private static DateTimeOffset RowTime(DateTime value)
        {
            return new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }

        public async Task<object> ObservationsAsync()
        {
            var stored = await organizations.ListAsync(CancellationToken.None);
            await using var db = await storage();
            var notifications = await db.Notifications.AsNoTracking().OrderBy(note => note.CreatedAt).ThenBy(note => note.Id).ToListAsync();
            var bindings = await db.Billing.AsNoTracking().ToListAsync();
            var singleton = await db.InternalBilling.AsNoTracking().SingleOrDefaultAsync();
            return new
            {
                now = now(),
                usageMode,
                usageRequests = usageRequests.ToArray(),
                scopeRequests = scopeRequests.ToArray(),
                stripeRequests = stripeRequests.ToArray(),
                pausedUsage,
                cliBuildFailures,
                organizations = stored.Select(organization => new { id = organization.Id, row = JsonSerializer.SerializeToElement(organization) }).ToArray(),
                bindings,
                singleton,
                notifications = notifications.Select(note => JsonSerializer.SerializeToElement(new { note.Id, note.OrganizationId, note.ProjectId, note.Type, note.BillingMonth, note.Title, note.Body, note.Severity, note.Status, note.Url, CreatedAt = RowTime(note.CreatedAt) })).ToArray()
            };
        }
    }
}
