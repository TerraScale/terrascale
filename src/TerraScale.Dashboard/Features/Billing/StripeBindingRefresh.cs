using Dapper;
using System.Data;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Clients;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed class StripeBindingRefresh(DashboardDb storage, StripeBilling billing, BillingSchemaGate gate, TimeProvider clock, ILogger<StripeBindingRefresh> logger, BackgroundJobs jobs) : BackgroundService
    {
        private const string StripeOrganizations = """
        SELECT organization_id FROM billing
        WHERE erasure_id IS NULL AND kind = 'stripe' AND state <> 'deleting' AND (subscription_id <> '' OR checkout_id <> '') AND (@After IS NULL OR organization_id > @After)
        ORDER BY organization_id LIMIT 200
        """;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromDays(1), clock);
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is DBConcurrencyException or TimeoutException or TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
                {
                    logger.StripeBindingRefreshIsUnavailableRetryingNext(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (!await gate.AllowsAsync(cancellationToken)) { return; }
            await jobs.RunAsync("stripe-refresh", TimeSpan.FromDays(1), async (claim, token) =>
            {
                string? after = null;
                while (true)
                {
                    List<string> organizationIds;
                    await using (var connection = await storage.OpenAsync(token))
                    {
                        organizationIds = (await connection.QueryAsync<string>(StripeOrganizations, new { After = after })).AsList();
                    }

                    foreach (var organizationId in organizationIds)
                    {
                        try
                        {
                            await billing.RefreshAsync(organizationId, token);
                        }
                        catch (Exception error) when (error is DBConcurrencyException or TimeoutException or TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or Stripe.StripeException)
                        {
                            logger.AStripeBindingCouldNotBeRefreshed(error.GetType().Name);
                        }
                    }
                    if (organizationIds.Count < 200) { break; }
                    after = organizationIds[^1];
                }
                await billing.CleanEventReceiptsAsync(token);
            }, cancellationToken, due: billing.RefreshDueAsync);
        }
    }
}
