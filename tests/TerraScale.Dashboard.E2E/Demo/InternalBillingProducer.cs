using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E;

// External test controls prepare provider-owned rows and inject storage/network
// failures. Journeys exercise the running app and its public CLI entry only.
sealed class InternalBillingProducer(TestState state, TestWorkspace workspace, Func<DateTimeOffset> now, Func<string> subject,
    Action<OrganizationRecord>? seedOrganization = null) : IDashboardState
{
    private readonly ConcurrentQueue<object> usageRequests = new();
    private readonly ConcurrentQueue<object> scopeRequests = new();
    private readonly ConcurrentQueue<string> stripeRequests = new();
    private readonly ConcurrentQueue<object> writes = new();
    private string usageMode = "coherent";
    private TaskCompletionSource? writeGate;
    private string pauseCollection = "";
    private string pauseId = "";
    private string pauseKind = "";
    private bool pauseAfter;
    private int pauseRemaining;
    private int pausedWrites;
    private string failCollection = "";
    private bool failAfter;
    private int failRemaining;
    private int failStatus;
    private int injectedFailures;
    private TaskCompletionSource? usageGate;
    private int pausedUsage;
    private int failCliBuild;
    private int cliBuildFailures;
    private string misboundOrganization = "";
    private int misboundOrganizationReads;

    public Task InitializeAsync(CancellationToken cancellationToken) => state.InitializeAsync(cancellationToken);
    public Task CheckReadyAsync(CancellationToken cancellationToken) => state.CheckReadyAsync(cancellationToken);
    public async Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
    {
        var document = await state.GetAsync(collection, id, cancellationToken);
        if (document is not null && collection == AccountService.OrganizationsCollection && id == misboundOrganization)
        {
            Interlocked.Increment(ref misboundOrganizationReads);
            return document with { Id = "foreign-producer-document-key" };
        }
        return document;
    }
    Task<TerraBaseDocumentPage> IDashboardState.ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken ct) => ((IDashboardState)state).ReadPageAsync(collection, filter, limit, cursor, ct);
    Task<TerraBaseTransactionResult> IDashboardState.TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken ct) => ((IDashboardState)state).TransactAsync(operations, ct);
    public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken cancellationToken) => state.ListAsync(collection, cancellationToken);
    public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken cancellationToken) => state.QueryPageAsync(collection, query, cancellationToken);
    public Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken) => state.DeleteAsync(collection, id, version, cancellationToken);

    public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken)
    {
        var kind = document.TryGetProperty("Kind", out var discriminator) ? discriminator.GetString() ?? "" : "";
        await PauseAsync(false);
        if (!failAfter) MaybeFail(collection);
        await state.PutAsync(collection, id, document, precondition, cancellationToken);
        if (collection is "dashboard_billing" or "dashboard_internal_billing" or "dashboard_organizations" or "dashboard_accounts")
            writes.Enqueue(new { collection, id, kind });
        await PauseAsync(true);
        if (failAfter) MaybeFail(collection);

        async Task PauseAsync(bool after)
        {
            if (after != pauseAfter || collection != pauseCollection || (pauseId.Length != 0 && id != pauseId) || (pauseKind.Length != 0 && kind != pauseKind)) return;
            if (Interlocked.Decrement(ref pauseRemaining) >= 0 && writeGate is { } gate)
            {
                Interlocked.Increment(ref pausedWrites);
                await gate.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private void MaybeFail(string collection)
    {
        if (collection == failCollection && Interlocked.Decrement(ref failRemaining) >= 0)
        {
            Interlocked.Increment(ref injectedFailures);
            if (failStatus != 0) throw new HttpRequestException("Injected test-only storage unavailability.", null, (System.Net.HttpStatusCode)failStatus);
            throw new IOException("Injected test-only internal billing storage interruption.");
        }
    }

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
                if (mode is not ("coherent" or "partial" or "foreign-project" or "wrong-month" or "incoherent-end" or "overflow" or "offline")) return Results.BadRequest();
                usageMode = mode;
                break;
            case "internal-pause-usage":
                pausedUsage = 0;
                usageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                break;
            case "internal-release-usage": usageGate?.TrySetResult(); usageGate = null; break;
            case "internal-fail-cli-build-once": Interlocked.Exchange(ref failCliBuild, 1); break;
            case "internal-misbind-organization-read":
                var organizationId = command.GetProperty("id").GetString()!;
                if (!TerraScaleIds.IsValid(organizationId)) return Results.BadRequest();
                misboundOrganization = organizationId;
                break;
            case "internal-pause-put":
                var collection = command.GetProperty("collection").GetString()!;
                if (!Allowed(collection)) return Results.BadRequest();
                Release();
                pauseCollection = collection;
                pauseId = command.TryGetProperty("id", out var id) ? id.GetString()! : "";
                pauseKind = command.TryGetProperty("kind", out var kind) ? kind.GetString()! : "";
                pauseAfter = command.TryGetProperty("after", out var committed) && committed.GetBoolean();
                pauseRemaining = command.TryGetProperty("count", out var remaining) ? remaining.GetInt32() : 1;
                if (pauseRemaining is < 1 or > 2) return Results.BadRequest();
                pausedWrites = 0;
                writeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                break;
            case "internal-release-put": Release(); break;
            case "internal-fail-put-once":
                failCollection = command.GetProperty("collection").GetString()!;
                if (!Allowed(failCollection)) return Results.BadRequest();
                failAfter = command.TryGetProperty("after", out var after) && after.GetBoolean();
                failStatus = command.TryGetProperty("status", out var status) ? status.GetInt32() : 0;
                failRemaining = 1;
                break;
            case "internal-clear-faults":
                Release(); failCollection = ""; failRemaining = 0; misboundOrganization = "";
                break;
            default: return Results.BadRequest();
        }
        return Results.Ok(new { configured = true });
    }

    public void Release()
    {
        writeGate?.TrySetResult();
        usageGate?.TrySetResult();
        usageGate = null;
        pauseCollection = "";
        pauseRemaining = 0;
    }

    private static bool Allowed(string collection) => collection is "dashboard_billing" or "dashboard_internal_billing" or "dashboard_organizations" or "dashboard_accounts" or "sessions";

    // Gives the signed-in account's active project one saved database, so reporting reads have something to report.
    private async Task<IResult> AddDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        var accountDocument = await state.GetAsync(AccountService.AccountsCollection, AccountService.AccountStorageKey(subject()), cancellationToken);
        if (accountDocument is null) return Results.BadRequest();
        var account = AccountService.Read<AccountRecord>(accountDocument);
        var organizationDocument = await state.GetAsync(AccountService.OrganizationsCollection, account.ActiveOrganizationId, cancellationToken);
        if (organizationDocument is null) return Results.BadRequest();
        var organization = AccountService.Read<OrganizationRecord>(organizationDocument);
        var project = organization.Projects[account.ActiveProjectId];
        var database = new DatabaseRecord { Id = new TerraScaleIdGenerator().NewId(), ProjectId = project.Id, Name = name, Region = "GRU", Regions = ["GRU"], CreatedAt = now() };
        project.Databases[database.Id] = database;
        await state.PutAsync(AccountService.OrganizationsCollection, organization.Id, JsonSerializer.SerializeToElement(organization), TerraBasePrecondition.Matches(organizationDocument.Version), cancellationToken);
        return Results.NoContent();
    }

    private async Task<IResult> SeedAsync(int count, bool paymentMethodValidated, CancellationToken cancellationToken)
    {
        if (count is < 1 or > 2) return Results.BadRequest();
        var owner = subject();
        var key = AccountService.AccountStorageKey(owner);
        var accountDocument = await state.GetAsync(AccountService.AccountsCollection, key, cancellationToken);
        if (accountDocument is null) return Results.BadRequest(new { error = "Sign in natively before preparing the pre-existing provider target." });
        var account = AccountService.Read<AccountRecord>(accountDocument);
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
            foreach (var name in new[] { "ts-dashboard", "ts-auth", "ts-router", "ts-status", "ts-orchestrator", "ku", "ts-reviewer" })
            {
                var project = new ProjectRecord { Id = ids.NewId(), OrganizationId = organization.Id, Name = name, Slug = name, Region = "GRU", Regions = ["GRU"], CreatedAt = now() };
                var database = new DatabaseRecord { Id = ids.NewId(), ProjectId = project.Id, Name = "app", Region = "GRU", Regions = ["GRU"], CreatedAt = now() };
                project.Databases[database.Id] = database;
                organization.Projects[project.Id] = project;
            }
            lock (workspace.Gate) workspace.Organizations[organization.Id] = TestWorkspace.Clone(organization);
            await state.PutAsync(AccountService.OrganizationsCollection, organization.Id, JsonSerializer.SerializeToElement(organization), TerraBasePrecondition.Absent, cancellationToken);
            seedOrganization?.Invoke(organization);
            account.OrganizationIds.Add(organization.Id);
            if (index == 0) { account.ActiveOrganizationId = organization.Id; account.ActiveProjectId = organization.Projects.Keys.First(); }
            targets.Add(new { organizationId = organization.Id, projects = organization.Projects.Values.Select(project => new { project.Id, project.Name }).ToArray() });
        }
        await state.PutAsync(AccountService.AccountsCollection, key, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Matches(accountDocument.Version), cancellationToken);
        return Results.Json(new { targets });
    }

    // TerraBase GET /v1/projects/{project}/usage (contracts/terrabase/fixtures/project-usage.json). The dispatcher has already
    // validated the exchanged project bearer and supplies its principal, organization and scopes.
    public async Task<IResult> UsageAsync(HttpContext http, string principal, string organizationId, string projectId, IReadOnlyList<string> scopes)
    {
        if (http.Request.Method != "GET" || http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries) is not ["v1", "projects", var routed, "usage"] || routed != projectId)
            return Error(HttpStatusCode.NotFound, "not_found", "Only project usage is produced by this fixture.");
        if (!scopes.Contains("databases:read")) return Error(HttpStatusCode.Forbidden, "forbidden", "databases:read is required.");
        // ts-auth owns identity, membership and projects; dashboard storage owns the database references.
        OrganizationRecord? live;
        lock (workspace.Gate) live = workspace.Organizations.GetValueOrDefault(organizationId) is { } found ? TestWorkspace.Clone(found) : null;
        if (live is null || !live.Projects.TryGetValue(projectId, out var project) || project.OrganizationId != organizationId)
            return Error(HttpStatusCode.NotFound, "not_found", "Project not found.");
        if (workspace.ScopedRole(principal, organizationId).Length == 0) return Error(HttpStatusCode.Forbidden, "forbidden", "Not a member of the organization.");
        var settings = state.Items.GetValueOrDefault(AccountService.OrganizationsCollection + "/" + organizationId)?.Document.Deserialize<OrganizationRecord>();
        var databases = settings?.Projects.GetValueOrDefault(projectId)?.Databases.Values.Where(database => database.ProjectId == projectId).OrderBy(database => database.CreatedAt).ToArray() ?? [];
        if (!Window(http.Request.Query, now(), out var granularity, out var from, out var to))
            return Error(HttpStatusCode.BadRequest, "invalid_request", "from and to must be RFC 3339 instants; granularity must be hour or day.");
        (granularity == "day" ? usageRequests : scopeRequests).Enqueue(new { organizationId, projectId, userId = principal, granularity, from, to });
        if (usageGate is { } gate)
        {
            Interlocked.Increment(ref pausedUsage);
            await gate.Task.WaitAsync(http.RequestAborted);
        }
        var ordinal = live.Projects.Keys.ToList().IndexOf(projectId);
        if (usageMode == "offline") return Error(HttpStatusCode.ServiceUnavailable, "unavailable", "Usage is unavailable.");
        if (usageMode == "partial" && ordinal == 0) return Error(HttpStatusCode.NotFound, "not_found", "Project usage not found.");
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
                collections = bytes > 0 ? 1 : 0,
                documents = bytes > 0 ? 17 : 0,
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
            documents = items.Sum(item => item.documents),
            reads = totalReads,
            writes = totalWrites,
            period = new { period.from, period.to, period.granularity, reads = totalReads, writes = totalWrites },
            series = Enumerable.Range(0, (int)((to - from).Ticks / step))
                .Select(index => new { start = from.AddTicks(index * step), reads = index == 0 ? totalReads : 0, writes = index == 0 ? totalWrites : 0 }).ToArray(),
            items
        });
    }

    private static IResult Error(HttpStatusCode status, string code, string message) => Results.Json(new { error = new { code, message } }, statusCode: (int)status);

    // TerraBase UsageWindow: UTC hour or day buckets, from rounded down, to (default now) rounded up, at most 840 hours or 400 days.
    private static bool Window(IQueryCollection query, DateTimeOffset current, out string granularity, out DateTimeOffset from, out DateTimeOffset to)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        granularity = query["granularity"].ToString() is "" or "hour" ? "hour" : query["granularity"].ToString();
        from = to = default;
        if (granularity is not ("hour" or "day")) return false;
        DateTimeOffset requestedTo = current, requestedFrom = default;
        if (query.TryGetValue("to", out var rawTo) && !DateTimeOffset.TryParseExact(rawTo, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out requestedTo)) return false;
        if (query.TryGetValue("from", out var rawFrom) && (!DateTimeOffset.TryParseExact(rawFrom, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out requestedFrom) || requestedFrom >= requestedTo)) return false;
        var ticks = granularity == "day" ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour;
        var end = (requestedTo.UtcTicks + ticks - 1) / ticks;
        var first = query.ContainsKey("from") ? requestedFrom.UtcTicks / ticks : end - (granularity == "hour" ? 24 : 30);
        if (end - first < 1 || end - first > (granularity == "hour" ? 840 : 400)) return false;
        from = new DateTimeOffset(first * ticks, TimeSpan.Zero);
        to = new DateTimeOffset(end * ticks, TimeSpan.Zero);
        return true;
    }

    public void ObserveStripe(HttpRequestMessage request) => stripeRequests.Enqueue(request.Method + " " + request.RequestUri!.AbsolutePath);

    public void BeforeCliBuild()
    {
        if (Interlocked.Exchange(ref failCliBuild, 0) == 1)
        {
            Interlocked.Increment(ref cliBuildFailures);
            throw new IOException("Injected test-only whole CLI application construction failure.");
        }
    }

    public object Observations() => new
    {
        now = now(),
        usageMode,
        usageRequests = usageRequests.ToArray(),
        scopeRequests = scopeRequests.ToArray(),
        stripeRequests = stripeRequests.ToArray(),
        pausedWrites,
        pausedUsage,
        cliBuildFailures,
        misboundOrganizationReads,
        injectedFailures,
        writes = writes.ToArray(),
        organizations = state.Items.Where(pair => pair.Key.StartsWith(AccountService.OrganizationsCollection + "/", StringComparison.Ordinal))
            .Select(pair => new { id = pair.Value.Id, document = pair.Value.Document }).ToArray(),
        bindings = state.Items.Where(pair => pair.Key.StartsWith("dashboard_billing/", StringComparison.Ordinal)).Select(pair => pair.Value.Document).ToArray(),
        singleton = state.Items.GetValueOrDefault("dashboard_internal_billing/organization")?.Document,
        notifications = state.Items.Where(pair => pair.Key.StartsWith(AccountService.AccountsCollection + "/", StringComparison.Ordinal))
            .SelectMany(pair => pair.Value.Document.GetProperty("Notifications").EnumerateArray()).ToArray()
    };
}
