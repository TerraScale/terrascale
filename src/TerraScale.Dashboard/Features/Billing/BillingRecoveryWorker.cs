using System.Data;
using System.Text.Json;
using Stripe;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing;

public sealed class BillingRecoveryWorker(StripeBilling billing, BillingSchemaGate gate, BackgroundJobs jobs, TimeProvider clock, ILogger<BillingRecoveryWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is DBConcurrencyException or TimeoutException or TerraBaseException or HttpRequestException or IOException or JsonException
                or InvalidOperationException or StripeException or OperationCanceledException)
            {
                BillingLog.RecoveryUnavailable(logger, error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await gate.AllowsAsync(cancellationToken)) { return; }
        await jobs.RunAsync("billing-recovery", Interval, async (_, token) =>
        {
            await billing.StampDraftOwnersAsync(token);
            await billing.ReconcileStaleChangesAsync(token);
            await billing.ReconcileAbandonedDraftsAsync(token);
        }, cancellationToken);
    }
}
