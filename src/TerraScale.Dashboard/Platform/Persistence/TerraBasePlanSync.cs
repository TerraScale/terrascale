using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Shared.Accounts;
using TerraScale.Dashboard.Shared.Databases;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Platform.Persistence;

// TerraBase enforces each organization's monthly operation and database allowances from the plan the platform publishes
// for its storage organization, whose id is the ts-auth organization id (PUT /admin/organizations/{organization}/plan on
// its private listener). The dashboard owns plans, so it publishes every organization's entitled plan: on each dashboard
// view and billing change, and from PlanReconciler. A changed plan is published at once. A settled one (TerraBase stored it,
// or answered 404 because there is nothing to set it on yet) is checked again every five minutes, so a wipe converges
// without re-sending every organization each 30 seconds; a failed publish retries with backoff. TerraBase keeps a paid
// plan published before the first database.
public sealed class TerraBasePlanSync(IHttpClientFactory http, IHostEnvironment environment, TimeProvider time, ILogger<TerraBasePlanSync> logger)
{
    // Request metadata identifies this publisher's storage-only writes; it is never an incoming header or setting.
    internal static readonly HttpRequestOptionsKey<PlanBody> Publication = new("TerraScale.StoragePlanPublication");
    // Views skip a publish this recent; the reconciler always publishes.
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    // A view never waits longer than this on TerraBase: the reconciler republishes within 30 seconds, while a
    // stalled private listener otherwise held sign-in callbacks until their 30-second request budget (QA newuser:8).
    private static readonly TimeSpan ViewBudget = TimeSpan.FromSeconds(2);
    // A settled plan is published again this often, which bounds how long a TerraBase wipe goes unnoticed.
    public static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
    // The reconciler retries a failed publish on its next pass, then waits this long, doubling per failure up to Refresh.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
    private readonly Uri origin = TerraBaseDashboardState.Origin(environment);
    private readonly ConcurrentDictionary<string, Attempt> published = new(StringComparer.Ordinal);

    // The last plan tried for an organization: settled when TerraBase stored it or had nothing to set it on (404).
    private sealed record Attempt(string Plan, DateTimeOffset At, bool Settled, int Failures)
    {
        public bool Due(string plan, DateTimeOffset now, bool reconcile) => Plan != plan || now - At >= (Settled ? Refresh
            : reconcile ? Failures <= 1 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(Refresh.Ticks, RetryAfter.Ticks << Math.Min(Failures - 2, 10))) : Fresh);
    }

    // TerraBase's tiers carry the published catalog's allowances (https://terrascale.tech/pricing.json), so an organization
    // gets the smallest catalog plan covering its operation and database allowances; an internal policy above every
    // catalog plan is enterprise. An unpaid organization (billing_required) keeps Hobby, so no allowance precedes payment.
    public static string PlanFor(OrganizationRecord organization)
    {
        if (organization.BillingStatus != "active") return "hobby";
        var operations = PlanCatalog.Limits(organization).MonthlyOperations;
        var databases = PlanCatalog.ResourceLimits(organization).Databases;
        var plan = PlanCatalog.Plans.OrderBy(plan => plan.Allowances.MonthlyOperations)
            .FirstOrDefault(plan => operations <= plan.Allowances.MonthlyOperations && databases <= plan.Resources.Databases);
        return plan is null ? "enterprise" : plan.Key == "free" ? "hobby" : plan.Key;
    }

    public Task PublishAsync(OrganizationRecord organization, CancellationToken cancellationToken) => PublishAsync(organization, false, cancellationToken);

    internal async Task PublishAsync(OrganizationRecord organization, bool reconcile, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatform(organization.Id) || !TerraScaleIds.IsValid(organization.Id)) return;
        string plan;
        // An invalid internal billing policy fails the billing pages themselves; publishing waits for a valid one.
        try { plan = PlanFor(organization); }
        catch (IOException) { return; }
        var now = time.GetUtcNow();
        var last = published.GetValueOrDefault(organization.Id);
        if (last is not null && !last.Due(plan, now, reconcile)) return;
        var failures = last is { Settled: false } && last.Plan == plan ? last.Failures : 0;
        // Views try a plan at most once per window, whatever the outcome: a failing TerraBase route otherwise cost every
        // page view its full budget (QA dashui:26/27/36).
        if (!reconcile) published[organization.Id] = new(plan, now, false, failures);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!reconcile) budget.CancelAfter(ViewBudget);
        var settled = false;
        try
        {
            using var response = await PutAsync($"admin/organizations/{organization.Id}/plan", plan, budget.Token);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                settled = response.IsSuccessStatusCode;
                if (!settled) logger.LogWarning("TerraBase refused plan {Plan} for organization {Organization}: {Status}", plan, organization.Id, (int)response.StatusCode);
                return;
            }
            // 404: Hobby on an organization with no record (its default), a deleted organization, or one TerraBase created
            // when a project token enrolled its first database, whose plan is set per enrolled project. A project 404
            // (unknown, or bound to another organization) leaves nothing to set either; only a refusal stays unsettled.
            settled = true;
            foreach (var project in organization.Projects.Keys)
            {
                using var enrolled = await PutAsync($"admin/projects/{project}/plan", plan, budget.Token);
                if (enrolled.IsSuccessStatusCode || enrolled.StatusCode == HttpStatusCode.NotFound) continue;
                settled = false;
                logger.LogWarning("TerraBase refused plan {Plan} for project {Project}: {Status}", plan, project, (int)enrolled.StatusCode);
            }
        }
        // The page, webhook or reconciler pass that triggered this still succeeds; a later one publishes again.
        catch (HttpRequestException error) { logger.LogWarning("TerraBase plan publish failed for organization {Organization} ({ErrorType})", organization.Id, error.GetType().Name); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { logger.LogWarning("TerraBase plan publish timed out for organization {Organization}", organization.Id); }
        finally { published[organization.Id] = new(plan, now, settled, settled ? 0 : failures + 1); }
    }

    private async Task<HttpResponseMessage> PutAsync(string path, string plan, CancellationToken cancellationToken)
    {
        var publication = new PlanBody(plan);
        // The private listener speaks only HTTP/2 without TLS, so the request needs prior knowledge, as the SDK sends it.
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(origin, path))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = JsonContent.Create(publication, PlanSyncJson.Default.PlanBody),
        };
        request.Options.Set(Publication, publication);
        return await http.CreateClient(TerraBaseDashboardState.ClientName).SendAsync(request, cancellationToken);
    }
}

public sealed record PlanBody(string Plan);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PlanBody))]
internal sealed partial class PlanSyncJson : JsonSerializerContext;
