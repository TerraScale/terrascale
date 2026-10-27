using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E.Features.Usage;

public sealed class UsageAllowanceContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 23, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ASpentFreeAllowanceSaysWritesArePausedUntilTheNextUtcMonth()
    {
        var free = PlanCatalog.Plans.Single(plan => plan.PriceCents == 0);
        var limit = free.Allowances.MonthlyOperations;
        await Assert.That(UsageService.WritesPaused(free, limit, limit, Now)).IsEqualTo(
            $"Writes are paused: this organization used all {limit:N0} operation units of the {free.Name} plan for October 2026. Reads and deletes keep working, and writes resume on November 1 (UTC).");
        await Assert.That(UsageService.WritesPaused(free, limit, limit - 1, Now)).IsEqualTo("").Because("Writes continue below the allowance.");
        await Assert.That(UsageService.WritesPaused(free, limit, null, Now)).IsEqualTo("").Because("Unmeasured usage is never reported as a pause.");
        await Assert.That(UsageService.WritesPaused(null, limit, limit, Now)).IsEqualTo("");
    }

    [Test]
    public async Task PaidPlansBillOverageInsteadOfPausingWrites()
    {
        foreach (var paid in PlanCatalog.Plans.Where(plan => plan.PriceCents > 0))
            await Assert.That(UsageService.WritesPaused(paid, paid.Allowances.MonthlyOperations, long.MaxValue - 1, Now)).IsEqualTo("");
    }
}
