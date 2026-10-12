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
sealed class InternalBillingProducer(TestState state, Func<DateTimeOffset> now, Func<string> subject,
    Action<OrganizationRecord>? seedOrganization = null) : IDashboardState
{
    private readonly ConcurrentQueue<object> usageRequests = new();
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

    private static bool Allowed(string collection) => collection is "dashboard_billing" or "dashboard_internal_billing" or "dashboard_organizations" or "dashboard_accounts";

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
                organization.Projects[project.Id] = project;
            }
            lock (TestWorkspace.Gate) TestWorkspace.Organizations[organization.Id] = TestWorkspace.Clone(organization);
            await state.PutAsync(AccountService.OrganizationsCollection, organization.Id, JsonSerializer.SerializeToElement(organization), TerraBasePrecondition.Absent, cancellationToken);
            seedOrganization?.Invoke(organization);
            account.OrganizationIds.Add(organization.Id);
            if (index == 0) { account.ActiveOrganizationId = organization.Id; account.ActiveProjectId = organization.Projects.Keys.First(); }
            targets.Add(new { organizationId = organization.Id, projects = organization.Projects.Values.Select(project => new { project.Id, project.Name }).ToArray() });
        }
        await state.PutAsync(AccountService.AccountsCollection, key, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Matches(accountDocument.Version), cancellationToken);
        return Results.Json(new { targets });
    }

    public async Task<IResult> UsageAsync(HttpContext http, string? activeSubject)
    {
        if (http.Request.Method != "GET" || activeSubject is null || activeSubject != subject()) return Results.Unauthorized();
        var parts = http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not ["v1", "management", "organizations", var organizationId, "projects", var projectId, "usage"] ||
            http.Request.Headers["X-TerraBase-User-Id"] != activeSubject || http.Request.Headers["X-TerraBase-Organization-Id"] != organizationId)
            return Results.BadRequest(new { error = "The usage producer requires exact native principal and organization scope." });
        if (!state.Items.TryGetValue(AccountService.OrganizationsCollection + "/" + organizationId, out var document)) return Results.NotFound();
        var organization = AccountService.Read<OrganizationRecord>(document);
        if (!organization.ContainsMember(activeSubject) || TestWorkspace.ScopedRole(activeSubject, organizationId).Length == 0 ||
            !organization.Projects.TryGetValue(projectId, out var project) || project.OrganizationId != organizationId) return Results.StatusCode(403);
        var query = http.Request.Query;
        if (query.Count != 2 || query["granularity"].ToString() != "day" ||
            !DateTimeOffset.TryParse(query["from"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var from) || from.Offset != TimeSpan.Zero)
            return Results.BadRequest(new { error = "Invalid exact usage period." });
        usageRequests.Enqueue(new { organizationId, projectId, userId = activeSubject, granularity = "day", from });
        var month = new DateTimeOffset(now().Year, now().Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = now() < from.AddMonths(1) ? now() : from.AddMonths(1);
        if (usageGate is { } gate)
        {
            Interlocked.Increment(ref pausedUsage);
            await gate.Task.WaitAsync(http.RequestAborted);
        }
        var ordinal = organization.Projects.Keys.ToList().IndexOf(projectId);
        if (usageMode == "offline") return Results.StatusCode(503);
        if (usageMode == "partial" && ordinal == 0) return Results.NotFound();
        long writes = ordinal == 0 ? 75_000_000 : 0;
        long logicalBytes = ordinal == 0 ? (5L << 40) + (2L << 30) : 0;
        if (usageMode == "overflow" && ordinal < 2) writes = long.MaxValue;
        return Results.Json(new
        {
            projectId = usageMode == "foreign-project" ? "foreign-project" : projectId,
            databaseId = "",
            logicalBytes,
            documents = ordinal == 0 ? 17 : 0,
            period = new
            {
                from = usageMode == "wrong-month" ? month.AddMonths(-1) : from,
                to = usageMode == "incoherent-end" && ordinal == 0 ? month.AddMonths(1).AddSeconds(1) : end,
                granularity = "day",
                reads = ordinal == 0 ? 123_456 : 0,
                writes
            },
            series = Array.Empty<object>(),
            items = Array.Empty<object>()
        });
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
