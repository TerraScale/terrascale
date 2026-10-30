using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Platform;

namespace TerraScale.Dashboard.Platform.Persistence
{
    // TerraBase enforces each organization's monthly operation and database allowances from the plan the platform publishes
    // for its storage organization, whose id is the ts-auth organization id (PUT /admin/organizations/{organization}/plan on
    // its private listener). The dashboard owns plans, so it publishes every organization's entitled plan: on each dashboard
    // view and billing change, and from PlanReconciler. A changed plan is published at once. A settled one (TerraBase stored it,
    // or answered 404 because there is nothing to set it on yet) is checked again every five minutes, so a wipe converges
    // without re-sending every organization each 30 seconds; a failed publish retries with backoff. TerraBase keeps a paid
    // plan published before the first database.
    public sealed class TerraBasePlanSync(TerraBaseRpcClient rpc, TimeProvider time, ILogger<TerraBasePlanSync> logger)
    {
        // Views skip a publish this recent; the reconciler always publishes.
        private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
        // A view never waits longer than this on TerraBase: the reconciler republishes within 30 seconds, while a
        // stalled private listener otherwise held sign-in callbacks until their 30-second request budget (QA newuser:8).
        private static readonly TimeSpan ViewBudget = TimeSpan.FromSeconds(2);
        // A settled plan is published again this often, which bounds how long a TerraBase wipe goes unnoticed.
        public static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
        // The reconciler retries a failed publish on its next pass, then waits this long, doubling per failure up to Refresh.
        private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
        private readonly ConcurrentDictionary<string, Attempt> published = new(StringComparer.Ordinal);

        // The last plan tried for an organization: settled when TerraBase stored it or had nothing to set it on (404).
        private sealed record Attempt(string Plan, DateTimeOffset At, bool Settled, int Failures)
        {
            public bool Due(string plan, DateTimeOffset now, bool reconcile)
            {
                return Plan != plan || now - At >= (Settled ? Refresh
                : reconcile ? Failures <= 1 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(Refresh.Ticks, RetryAfter.Ticks << Math.Min(Failures - 2, 10))) : Fresh);
            }
        }

        // TerraBase's tiers carry the published catalog's allowances (https://terrascale.tech/pricing.json), so an organization
        // gets the smallest catalog plan covering its operation and database allowances; an internal policy above every
        // catalog plan is enterprise. An unpaid organization (billing_required) keeps Hobby, so no allowance precedes payment.
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

        public Task PublishAsync(OrganizationRecord organization, CancellationToken cancellationToken)
        {
            return PublishAsync(organization, false, cancellationToken);
        }

        internal async Task PublishAsync(OrganizationRecord organization, bool reconcile, CancellationToken cancellationToken)
        {
            if (PlatformDatabases.IsPlatform(organization.Id) || !TerraScaleIds.IsValid(organization.Id))
            {
                return;
            }

            string plan;
            // An invalid internal billing policy fails the billing pages themselves; publishing waits for a valid one.
            try { plan = PlanFor(organization); }
            catch (IOException) { return; }
            var now = time.GetUtcNow();
            var last = published.GetValueOrDefault(organization.Id);
            if (last is not null && !last.Due(plan, now, reconcile))
            {
                return;
            }

            var failures = last is { Settled: false } && last.Plan == plan ? last.Failures : 0;
            // Views try a plan at most once per window, whatever the outcome: a failing TerraBase route otherwise cost every
            // page view its full budget (QA dashui:26/27/36).
            if (!reconcile)
            {
                published[organization.Id] = new(plan, now, false, failures);
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!reconcile)
            {
                budget.CancelAfter(ViewBudget);
            }

            var settled = false;
            try
            {
                var response = await PutAsync(organization.Id, plan, false, budget.Token);
                if (response != 404)
                {
                    settled = response is >= 200 and <= 299;
                    if (!settled)
                    {
                        logger.TerraBaseRefusedPlanForOrganization(plan, organization.Id, response);
                    }

                    return;
                }
                // 404: Hobby on an organization with no record (its default), a deleted organization, or one TerraBase created
                // when a project token enrolled its first database, whose plan is set per enrolled project. A project 404
                // (unknown, or bound to another organization) leaves nothing to set either; only a refusal stays unsettled.
                settled = true;
                foreach (var project in organization.Projects.Keys)
                {
                    var enrolled = await PutAsync(project, plan, true, budget.Token);
                    if (enrolled is >= 200 and <= 299 or 404)
                    {
                        continue;
                    }

                    settled = false;
                    logger.TerraBaseRefusedPlanForProject(plan, project, enrolled);
                }
            }
            // The page, webhook or reconciler pass that triggered this still succeeds; a later one publishes again.
            catch (Exception error) when (error is HttpRequestException or TerraBaseException) { logger.TerraBasePlanPublishFailedForOrganization(organization.Id, error.GetType().Name); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { logger.TerraBasePlanPublishTimedOutFor(organization.Id); }
            finally { published[organization.Id] = new(plan, now, settled, settled ? 0 : failures + 1); }
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
