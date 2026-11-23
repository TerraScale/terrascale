using System.Data;
using Dapper;
using TerraScale.Results;
using TerraScale.Dashboard.Platform.Clients;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public sealed class TerraBasePlanSync(ControlPlaneClient control, ILogger<TerraBasePlanSync> logger, BackgroundJobs jobs,
        DashboardDb storage, TimeProvider time)
    {
        private const string PlanJobs = DashboardSql.BackgroundJobs.Select + " WHERE id LIKE 'plan/%'";
        private const string JobById = DashboardSql.BackgroundJobs.Select + " WHERE id = @Id";
        private const string LiveOrganization = "SELECT count(*) FROM organizations WHERE id = @Id AND erasure_id IS NULL";
        private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
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
            var response = await PutAsync(organization.Id, PlanFor(organization), false, budget.Token);
            if (response is < 200 or > 299) { throw new IOException("Organization plan publication failed."); }
        }

        public async Task<bool> AnyDueAsync(IReadOnlyList<OrganizationRecord> organizations, CancellationToken cancellationToken)
        {
            List<BackgroundJobRow> states;
            await using (var connection = await storage.OpenAsync(cancellationToken))
            {
                states = (await connection.QueryAsync<BackgroundJobRow>(PlanJobs)).AsList();
            }

            var now = time.GetUtcNow().UtcDateTime;
            foreach (var organization in organizations)
            {
                if (PlatformDatabases.IsPlatform(organization.Id) || !TerraScaleIds.IsValid(organization.Id)) { continue; }
                string plan;
                try { plan = PlanFor(organization); }
                catch (IOException) { continue; }
                var state = states.SingleOrDefault(row => row.Id == "plan/" + organization.Id);
                if (state is null || Due(state, plan, now))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Due(BackgroundJobRow state, string plan, DateTime now) =>
            DateTime.SpecifyKind(state.LeaseUntil, DateTimeKind.Utc) <= now
            && (state.Cursor != plan && state.Cursor != plan + "/projects" || DateTime.SpecifyKind(state.AvailableAt, DateTimeKind.Utc) <= now);

        internal async Task PublishAsync(OrganizationRecord organization, bool reconcile, CancellationToken cancellationToken)
        {
            if (PlatformDatabases.IsPlatform(organization.Id) || !TerraScaleIds.IsValid(organization.Id)) { return; }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!reconcile) { budget.CancelAfter(ViewBudget); }
            try
            {
                var plan = PlanFor(organization);
                var id = "plan/" + organization.Id;
                BackgroundJobRow? state;
                await using (var connection = await storage.OpenAsync(budget.Token))
                {
                    state = await connection.QuerySingleOrDefaultAsync<BackgroundJobRow>(JobById, new { Id = id });
                }

                if (state is not null && !Due(state, plan, time.GetUtcNow().UtcDateTime))
                {
                    return;
                }

                await jobs.RunAsync(id, TimeSpan.Zero, async (claim, token) =>
                {
                    if (await claim.Connection.ExecuteScalarAsync<long>(LiveOrganization, new { organization.Id }) == 0)
                    {
                        await claim.ForgetAsync(null, token);
                        return;
                    }

                    var row = claim.State;
                    row.Failures = row.Cursor == plan || row.Cursor == plan + "/projects" ? row.Failures + 1 : 1;
                    row.Cursor = plan;
                    row.AttemptedAt = claim.Now;
                    row.SucceededAt = null;
                    claim.Delay = Backoff(row.Failures, reconcile);
                    await claim.SaveAsync(token);
                    var settled = false;
                    try
                    {
                        var response = await PutAsync(organization.Id, plan, false, token);
                        if (response == 404)
                        {
                            row.Cursor = plan + "/projects";
                            settled = true;
                            foreach (var project in organization.Projects.Keys)
                            {
                                var enrolled = await PutAsync(project, plan, true, token);
                                if (enrolled is >= 200 and <= 299 or 404) { continue; }
                                settled = false;
                                logger.TerraBaseRefusedPlanForProject(plan, project, enrolled);
                            }
                        }
                        else
                        {
                            settled = response is >= 200 and <= 299;
                            if (!settled) { logger.TerraBaseRefusedPlanForOrganization(plan, organization.Id, response); }
                        }
                    }
                    finally
                    {
                        if (settled)
                        {
                            row.SucceededAt = claim.Now;
                            row.Failures = 0;
                            claim.Delay = Refresh;
                        }
                        await claim.SaveAsync(CancellationToken.None);
                    }
                }, budget.Token, plan);
            }
            catch (Exception error) when (error is HttpRequestException or TerraBaseException or IOException or DBConcurrencyException)
            {
                logger.TerraBasePlanPublishFailedForOrganization(organization.Id, error.GetType().Name);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.TerraBasePlanPublishTimedOutFor(organization.Id);
            }
        }

        private static TimeSpan Backoff(int failures, bool reconcile) =>
            !reconcile ? Fresh : failures <= 1 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(Refresh.Ticks, RetryAfter.Ticks << Math.Min(failures - 2, 10)));

        private async Task<int> PutAsync(string id, string plan, bool project, CancellationToken cancellationToken)
        {
            var published = project
                ? await control.SetProjectPlanAsync(id, plan, cancellationToken)
                : await control.SetOrganizationPlanAsync(id, plan, cancellationToken);
            return published switch
            {
                Done => StatusCodes.Status200OK,
                TerraBaseRefusal refusal => refusal.Status,
            };
        }

    }

}
