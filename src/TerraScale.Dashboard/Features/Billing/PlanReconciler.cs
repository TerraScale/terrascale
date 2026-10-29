using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing
{
    // Converges TerraBase to every organization's entitled plan every 30 seconds, whatever was missed: a TerraBase wipe or
    // re-seed, an enrollment after the last billing change, a failed publish, or a paid checkout whose webhook never came.
    public sealed class PlanReconciler(IDashboardState state, StripeBilling billing, TerraBasePlanSync plans, TimeProvider clock, ILogger<PlanReconciler> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
                {
                    logger.PlanReconciliationIsUnavailableRetryingIn(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            // A pending checkout first: its refresh grants and publishes the plan the organization now pays for.
            foreach (var document in await state.ListAsync(StripeBilling.Collection, cancellationToken))
            {
                try
                {
                    if (document.State == "value" && document.Document.Deserialize<BillingBinding>() is { } binding)
                    {
                        await billing.ReconcilePendingCheckoutAsync(binding, cancellationToken);
                    }
                }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or Stripe.StripeException)
                {
                    logger.APendingCheckoutDidNotReconcile(error.GetType().Name);
                }
            }
            foreach (var document in await state.ListAsync(AccountService.OrganizationsCollection, cancellationToken))
            {
                OrganizationRecord organization;
                try { organization = AccountService.Read<OrganizationRecord>(document); }
                catch (Exception error) when (error is IOException or JsonException) { continue; }
                await plans.PublishAsync(organization, reconcile: true, cancellationToken);
            }
        }
    }
}
