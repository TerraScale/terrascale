using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Shared.Accounts;
using TerraScale.Dashboard.Shared.Databases;

namespace TerraScale.Dashboard.Platform.Persistence;

// TerraBase enforces each project's monthly operation allowance from the plan the platform publishes for the
// storage organization the project enrolled (PUT /admin/projects/{project}/plan on its private listener). The
// dashboard owns plans, so it publishes the organization's entitled plan to every project it knows.
// shortcut: a project whose first database is created outside the dashboard gets the plan on the organization's
// next dashboard view or billing change (TerraBase answers 404 until a database enrolls it); push from ts-auth
// project events once the dashboard receives them.
public sealed class TerraBasePlanSync(IHttpClientFactory http, IHostEnvironment environment, TimeProvider time, ILogger<TerraBasePlanSync> logger)
{
    // Re-publish after this long so a TerraBase reset or a missed write converges without a restart.
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private readonly Uri origin = TerraBaseDashboardState.Origin(environment);
    private readonly ConcurrentDictionary<string, (string Plan, DateTimeOffset At)> published = new(StringComparer.Ordinal);

    // TerraBase plan tiers by monthly 4 KiB operation units (https://terrascale.tech/pricing.json): hobby 100K, starter 1M, pro 100M.
    // An unpaid organization (billing_required) keeps Hobby, so no allowance is granted before payment.
    public static string PlanFor(OrganizationRecord organization)
    {
        if (organization.BillingStatus != "active") return "hobby";
        return PlanCatalog.Limits(organization).MonthlyOperations switch
        {
            <= 100_000 => "hobby",
            <= 1_000_000 => "starter",
            <= 100_000_000 => "pro",
            _ => "enterprise",
        };
    }

    public async Task PublishAsync(OrganizationRecord organization, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatform(organization.Id)) return;
        string plan;
        // An invalid internal billing policy fails the billing pages themselves; publishing waits for a valid one.
        try { plan = PlanFor(organization); }
        catch (IOException) { return; }
        var now = time.GetUtcNow();
        foreach (var project in organization.Projects.Keys)
        {
            if (published.TryGetValue(project, out var last) && last.Plan == plan && now - last.At < Fresh) continue;
            try
            {
                // The private listener speaks only HTTP/2 without TLS, so the request needs prior knowledge, as the SDK sends it.
                using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(origin, $"admin/projects/{project}/plan"))
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = JsonContent.Create(new PlanBody(plan), PlanSyncJson.Default.PlanBody),
                };
                using var response = await http.CreateClient(TerraBaseDashboardState.ClientName).SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode) published[project] = (plan, now);
                else if (response.StatusCode != HttpStatusCode.NotFound)
                    logger.LogWarning("TerraBase refused plan {Plan} for project {Project}: {Status}", plan, project, (int)response.StatusCode);
            }
            // The page or webhook that triggered this still succeeds; the next view or change publishes again.
            catch (HttpRequestException error) { logger.LogWarning("TerraBase plan publish failed for project {Project} ({ErrorType})", project, error.GetType().Name); }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { logger.LogWarning("TerraBase plan publish timed out for project {Project}", project); }
        }
    }
}

public sealed record PlanBody(string Plan);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PlanBody))]
internal sealed partial class PlanSyncJson : JsonSerializerContext;
