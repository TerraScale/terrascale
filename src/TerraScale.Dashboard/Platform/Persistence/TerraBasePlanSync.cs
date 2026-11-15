using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Platform;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public sealed class TerraBasePlanSync(TerraBaseRpcClient rpc, TimeProvider time, ILogger<TerraBasePlanSync> logger, BackgroundJobs jobs,
        IDbContextFactory<DashboardDbContext> storage)
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ViewBudget = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);

        public static string PlanFor(OrganizationRecord organization)
        {
            if (organization.BillingStatus != "active")
            {
                return "hobby";
            }

            var operations = PlanCatalog.Limits(organization).MonthlyOperations;
            var databases = PlanCatalog.ResourceLimits(organization).Databases;
            var plan = PlanCatalog.Plans.OrderBy(plan => plan.Allowances.MonthlyOperations)
                .FirstOrDefault(plan => operations <= plan.Allowances.MonthlyOperations && databases <= plan.Resources.Databases);
            return plan is null ? "enterprise" : plan.Key == "free" ? "hobby" : plan.Key;
        }

        public async Task PublishAsync(OrganizationRecord organization, CancellationToken cancellationToken)
        {
            await PublishAsync(organization, false, cancellationToken);
        }

        public async Task EnsurePublishedAsync(OrganizationRecord organization, CancellationToken cancellationToken)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            var completed = await PublishClaimedAsync(organization, true, true, budget.Token);
            if (!completed)
            {
                await using var db = await storage.CreateDbContextAsync(cancellationToken);
                var id = "plan/" + organization.Id;
                var plan = PlanFor(organization);
                if (!await db.BackgroundJobs.AnyAsync(row => row.Id == id && row.Cursor == plan && row.SucceededAt != null, cancellationToken))
                {
                    throw new DbUpdateConcurrencyException("Organization plan publication is claimed by another dashboard instance.");
                }
            }
        }

        internal async Task PublishAsync(OrganizationRecord organization, bool reconcile, CancellationToken cancellationToken)
        {
            if (PlatformDatabases.IsPlatform(organization.Id) || !TerraScaleIds.IsValid(organization.Id)) { return; }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!reconcile) { budget.CancelAfter(ViewBudget); }
            try { await PublishClaimedAsync(organization, reconcile, false, budget.Token); }
            catch (Exception error) when (error is HttpRequestException or TerraBaseException or IOException)
            {
                logger.TerraBasePlanPublishFailedForOrganization(organization.Id, error.GetType().Name);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.TerraBasePlanPublishTimedOutFor(organization.Id);
            }
        }

        private async Task<bool> PublishClaimedAsync(OrganizationRecord organization, bool reconcile, bool required, CancellationToken cancellationToken)
        {
            var plan = PlanFor(organization);
            return await jobs.RunAsync("plan/" + organization.Id, TimeSpan.Zero, async (claim, token) =>
            {
                var state = claim.State;
                var now = time.GetUtcNow().UtcDateTime;
                if (!required && (state.Cursor == plan || state.Cursor == plan + "/projects") && (state.SucceededAt is { } success && now - success < Refresh ||
                    !reconcile && state.AttemptedAt is { } attempted && now - attempted < Fresh)) { return; }
                state.Cursor = plan;
                state.AttemptedAt = now;
                state.SucceededAt = null;
                await claim.SaveAsync(token);
                var response = await PutAsync(organization.Id, plan, false, token);
                if (response == 404)
                {
                    if (required) { throw new IOException("Organization plan publication failed."); }
                    state.Cursor = plan + "/projects";
                    foreach (var project in organization.Projects.Keys)
                    {
                        var enrolled = await PutAsync(project, plan, true, token);
                        if (enrolled is not (>= 200 and <= 299 or 404))
                        {
                            throw new IOException("Project plan publication failed.");
                        }
                    }
                }
                else if (response is < 200 or > 299)
                {
                    throw new IOException("Organization plan publication failed.");
                }
                state.SucceededAt = now;
                await claim.SaveAsync(token);
            }, cancellationToken);
        }

        private async Task<int> PutAsync(string id, string plan, bool project, CancellationToken cancellationToken)
        {
            var request = JsonSerializer.SerializeToElement(new PlanBody(plan), PlanSyncJson.Default.PlanBody);
            var client = rpc.Platform<IPlatformService>().WithCancellationToken(cancellationToken);
            try
            {
                if (project) { await client.SetProjectPlanAsync(id, request); }
                else { await client.SetOrganizationPlanAsync(id, request); }
                return 200;
            }
            catch (TerraBaseException error) { return error.Status; }
        }

    }

    public sealed record PlanBody(string Plan);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(PlanBody))]
    internal sealed partial class PlanSyncJson : JsonSerializerContext;
}
