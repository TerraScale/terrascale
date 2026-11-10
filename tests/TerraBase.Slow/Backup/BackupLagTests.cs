using System.Globalization;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class BackupLagTests
{
    [Test]
    public async Task Failed_upload_after_a_success_keeps_lag_at_the_last_shipped_age(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        await host.PutAsync("people", "one", "first", cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(2), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(2), cancellationToken);
        var caughtUp = host.Status();
        await Assert.That(caughtUp.LastShippedAt).IsNotNull();
        await Assert.That(caughtUp.LastError).IsNull();
        await Assert.That(caughtUp.LagSeconds).IsEqualTo(0);
        await host.PutAsync("people", "two", "second", cancellationToken);
        host.S3.FailPut = key => key.StartsWith(host.File + "/wal/", StringComparison.Ordinal);
        await host.ShipAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var lagged = host.Status();
        await Assert.That(lagged.LastError).IsNotNull();
        await Assert.That(lagged.LagSeconds).IsGreaterThan(10);
        var line = (await host.MetricsAsync(cancellationToken)).Split('\n')
            .Single(row => row.StartsWith("terrabase_backup_lag_seconds{", StringComparison.Ordinal));
        var published = double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture);
        await Assert.That(published).IsGreaterThan(10);
    }
}
