using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E
{
    // The captured Go host has no metering or replication producer. These test-only
    // dependency observations contain no counters; storage still uses real reads.
    public sealed class ReferenceUsageMeasurements : IUsageMeasurements
    {
        public Task<UsageMeasurements> ReadAsync(UsageSelection selection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UsageMeasurements(new(UsageSourceState.NoProducer),
                selection.Context.Organization!.Projects.Keys.ToDictionary(id => id, _ => new UsageObservation(UsageSourceState.NoProducer), StringComparer.Ordinal)));
        }
    }

    public sealed class ReferenceUsageReplication : IUsageReplication
    {
        public Task<UsageReplication> ReadAsync(UsageSelection selection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UsageReplication([], [], Empty: "Replication measurements are unavailable."));
        }
    }
}
