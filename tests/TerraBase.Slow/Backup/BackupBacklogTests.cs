using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class BackupBacklogTests
{
    [Test]
    public async Task A_backlog_over_256_MiB_after_a_slow_snapshot_upload_keeps_its_generation_and_restores_exactly(CancellationToken cancellationToken)
    {
        // About 288 MB of change log: over the 256 MiB that used to drop every new generation, in nine read budgets.
        await using var host = await BackupShippingHost.StartAsync();
        await BackupBacklogScenario.RunAsync(host, writes: 320, minimumSegments: 8, cancellationToken);
    }
}
