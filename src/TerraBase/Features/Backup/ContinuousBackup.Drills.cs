using System.Diagnostics.Metrics;
using System.Text.Json;

namespace TerraBase.Features.Backup;

internal sealed partial class ContinuousBackup
{
    private sealed record RecoveryObservation(DateTimeOffset At, double AgeSeconds, long ObservedAt);

    private IEnumerable<ShippedFile> DrillFiles() =>
        _files.Values.Where(file => !file.Removed && (!file.LeaseKnown || file.Shipping) && file.Target.IncludesPlatform);

    private void RegisterDrillMetrics(Meter meter)
    {
        var region = new KeyValuePair<string, object?>("region", _region);
        meter.CreateObservableGauge("terrabase.backup.recovery_point_age",
            () => new Measurement<double>(DrillFiles().Select(file => file.RecoveryPoint is { } point
                ? point.AgeSeconds + _time.GetElapsedTime(point.ObservedAt).TotalSeconds : double.PositiveInfinity).DefaultIfEmpty().Max(), region),
            "s", "Age of the oldest proven platform recovery point, advancing without bucket access; infinity means unproven.");
        meter.CreateObservableGauge("terrabase.backup.drill_last_success",
            () => new Measurement<long>(DrillFiles().Select(file => file.LastDrill is { Passed: true } drill ? drill.At.ToUnixTimeSeconds() : 0).DefaultIfEmpty().Min(), region),
            "s", "Unix time of the oldest successful latest restore drill; zero means missing or failed.");
        meter.CreateObservableGauge("terrabase.backup.drill_failed_files",
            () => new Measurement<int>(DrillFiles().Count(file => file.LastDrill is { Passed: false }), region),
            description: "Files whose latest platform restore drill or result commit failed.");
    }

    private void ObserveRecoveryPoint(ShippedFile file, DateTimeOffset at)
    {
        if (file.RecoveryPoint is null || file.RecoveryPoint.At < at)
        {
            file.RecoveryPoint = new(at, Math.Max(0, (_time.GetUtcNow() - at).TotalSeconds), _time.GetTimestamp());
        }
    }

    private static bool DrillDue(ShippedFile file, DateTimeOffset at) =>
        at.UtcDateTime.Date > file.DrilledAt.UtcDateTime.Date;

    private async Task RecoverDrillAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var store = await _storage.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var (kind, id) = RecordKey(file.Database);
        var saved = await transaction.GetAsync(kind, id, cancellationToken).ConfigureAwait(false);
        if (saved is not null
            && JsonSerializer.Deserialize(saved.Json.Span, BackupJsonContext.Default.BackupStatusRecord) is { } record
            && record.File == file.Prefix && record.Member == _node
            && record.LastDrill is { } drill && drill.Region == _region && drill.Destination == file.Target.Revision)
        {
            file.LastDrill = drill;
            file.DrilledAt = drill.At;
        }
    }
}
