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
// view and billing change, and from PlanReconciler every 30 seconds, so a wipe, a missed write or an enrollment after
// the last change converges within a minute. TerraBase keeps a paid plan published before the first database.
public sealed class TerraBasePlanSync(IHttpClientFactory http, IHostEnvironment environment, TimeProvider time, ILogger<TerraBasePlanSync> logger)
{
    // Request metadata identifies this publisher's storage-only writes; it is never an incoming header or setting.
    internal static readonly HttpRequestOptionsKey<PlanBody> Publication = new("TerraScale.StoragePlanPublication");
    // Views skip a publish this recent; the reconciler always publishes.
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private readonly Uri origin = TerraBaseDashboardState.Origin(environment);
    private readonly ConcurrentDictionary<string, (string Plan, DateTimeOffset At)> published = new(StringComparer.Ordinal);

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
        if (!reconcile && published.TryGetValue(organization.Id, out var last) && last.Plan == plan && now - last.At < Fresh) return;
        try
        {
            using var response = await PutAsync($"admin/organizations/{organization.Id}/plan", plan, cancellationToken);
            // 404 is Hobby for an organization TerraBase has no storage for yet: Hobby is its default.
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound && plan == "hobby" && organization.Projects.Count == 0)
            {
                published[organization.Id] = (plan, now);
                return;
            }
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                logger.LogWarning("TerraBase refused plan {Plan} for organization {Organization}: {Status}", plan, organization.Id, (int)response.StatusCode);
                return;
            }
            // shortcut: a TerraBase without the organization route answers 404; publish to each enrolled project as before
            // until every region serves it.
            var enrolled = true;
            foreach (var project in organization.Projects.Keys)
            {
                using var fallback = await PutAsync($"admin/projects/{project}/plan", plan, cancellationToken);
                enrolled &= fallback.IsSuccessStatusCode || fallback.StatusCode == HttpStatusCode.NotFound;
            }
            if (enrolled) published[organization.Id] = (plan, now);
        }
        // The page, webhook or reconciler pass that triggered this still succeeds; the next one publishes again.
        catch (HttpRequestException error) { logger.LogWarning("TerraBase plan publish failed for organization {Organization} ({ErrorType})", organization.Id, error.GetType().Name); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { logger.LogWarning("TerraBase plan publish timed out for organization {Organization}", organization.Id); }
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
