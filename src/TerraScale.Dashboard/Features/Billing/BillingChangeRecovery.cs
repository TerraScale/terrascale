using System.Data;
using Dapper;
using Stripe;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Billing;

public sealed partial class StripeBilling
{
    internal static readonly TimeSpan StaleChangeAge = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan UnverifiableChangeAge = TimeSpan.FromDays(7);
    private const int StaleChangesPerPass = 100;
    private const string StaleChanges = DashboardSql.BillingChanges.Select + """
         WHERE status IN ('reserved', 'applying') AND status_changed_at < @Before AND (@After IS NULL OR id > @After)
        ORDER BY id LIMIT @Limit
        """;
    private const string StampUnaged = """
        UPDATE billing_changes SET status_changed_at = @Now
        WHERE id IN (SELECT id FROM billing_changes WHERE status IN ('reserved', 'applying') AND status_changed_at IS NULL LIMIT 1024)
        """;
    private string? staleChangeCursor;

    public async Task ReconcileStaleChangesAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        List<BillingChangeReceipt> stale;
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(StampUnaged, new { Now = now });
            stale = (await connection.QueryAsync<BillingChangeReceipt>(StaleChanges,
                new { Before = now - StaleChangeAge, After = Volatile.Read(ref staleChangeCursor), Limit = StaleChangesPerPass })).AsList();
        }

        Volatile.Write(ref staleChangeCursor, stale.Count < StaleChangesPerPass ? null : stale[^1].Id);
        foreach (var change in stale)
        {
            bool completed;
            try
            {
                completed = await ReconcileStaleChangeAsync(change, cancellationToken);
            }
            catch (Exception error) when (error is AccountAccessException || error is StripeException stripe && MissingStripeObject(stripe))
            {
                await AbandonStaleChangeAsync(change, "unverifiable_" + (error is StripeException ? "subscription_missing" : "subscription_state"));
                continue;
            }
            catch (Exception error) when (error is DBConcurrencyException or StripeException or HttpRequestException or IOException or TerraBaseException or TimeoutException
                || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                BillingLog.StaleChangeFailed(logger, change.OrganizationId, change.RequestId, error.GetType().Name);
                if (change.StatusChangedAt is { } changedAt && now - changedAt > UnverifiableChangeAge)
                {
                    await AbandonStaleChangeAsync(change, "unverifiable_" + error.GetType().Name);
                }

                continue;
            }

            if (completed)
            {
                await RefreshCompletedChangeAsync(change, cancellationToken);
            }
        }
    }

    private async Task RefreshCompletedChangeAsync(BillingChangeReceipt change, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(change.OrganizationId, cancellationToken);
        }
        catch (Exception error) when (error is DBConcurrencyException or StripeException or HttpRequestException or IOException or TerraBaseException or TimeoutException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.AStripeBindingCouldNotBeRefreshed(error.GetType().Name);
        }
    }

    private async Task<bool> ReconcileStaleChangeAsync(BillingChangeReceipt change, CancellationToken cancellationToken)
    {
        var stored = await organizations.FindAsync(change.OrganizationId, cancellationToken);
        var snapshot = stored is { Erased: false } ? await PlanSnapshotAsync(stored.Organization, cancellationToken) : null;
        if (snapshot is not null && snapshot.Subscription.Id == change.SubscriptionId && Realized(change, snapshot.State))
        {
            await FinishChangeAsync(change.OrganizationId, change, cancellationToken);
            BillingLog.StaleChangeReconciled(logger, change.OrganizationId, change.RequestId, "completed");
            telemetry.ReportReconciledStaleChange();
            return true;
        }

        if (change.Status == "applying" && snapshot is { Subscription.ScheduleId: { Length: > 0 } scheduleId, State.ScheduledPlanKey.Length: 0 })
        {
            await ReleaseOrphanScheduleAsync(snapshot.Client, scheduleId, cancellationToken);
        }

        await AbandonStaleChangeAsync(change, snapshot is null ? "no_subscription" : "not_applied");
        return false;
    }

    private async Task AbandonStaleChangeAsync(BillingChangeReceipt change, string reason)
    {
        if (await AbandonChangeAsync(change.OrganizationId, change))
        {
            BillingLog.StaleChangeReconciled(logger, change.OrganizationId, change.RequestId, "abandoned: " + reason);
            telemetry.ReportReconciledStaleChange();
        }
    }
}
