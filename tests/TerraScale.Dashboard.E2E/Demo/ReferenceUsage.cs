using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E
{
    // The captured Go host has no metering producer. This test-only
    // dependency observation contains no counters.
    public sealed class ReferenceUsageMeasurements : IUsageMeasurements
    {
        public Task<UsageMeasurements> ReadAsync(UsageSelection selection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UsageMeasurements(new(UsageSourceState.NoProducer),
                selection.Context.Organization!.Projects.Keys.ToDictionary(id => id, _ => new UsageObservation(UsageSourceState.NoProducer), StringComparer.Ordinal)));
        }
    }
}
