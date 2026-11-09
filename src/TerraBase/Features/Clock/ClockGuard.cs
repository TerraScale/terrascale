using System.Diagnostics.Metrics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;

namespace TerraBase.Features.Clock;

internal sealed record ClockReport(string Node, string Region, double? SkewMilliseconds, bool LocalWritesAllowed, DateTimeOffset SampledAt);

internal sealed class ClockGuard(
    TerraBaseOptions options, TimeProvider time, IMeterFactory meters, IHlcState state, IClockErrorSource errors) : BackgroundService, ILocalWriteClock
{
    private const double LimitMilliseconds = 50;
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(15);
    private readonly TimeProvider _elapsed = TimeProvider.System;
    private Reading? _reading;
    private int _reported;

    public bool Allowed => SkewMilliseconds <= LimitMilliseconds;

    public double SkewMilliseconds
    {
        get
        {
            var reading = Volatile.Read(ref _reading);
            if (reading is null || _elapsed.GetElapsedTime(reading.At) > FreshFor)
            {
                return double.PositiveInfinity;
            }

            var before = _elapsed.GetTimestamp();
            var utc = time.GetUtcNow().ToUnixTimeMilliseconds();
            var after = _elapsed.GetTimestamp();
            var lower = utc - reading.Utc - _elapsed.GetElapsedTime(reading.At, after).TotalMilliseconds - 1;
            var upper = utc - reading.Utc - _elapsed.GetElapsedTime(reading.After, before).TotalMilliseconds + 1;
            var jump = Math.Max(Math.Abs(lower), Math.Abs(upper));
            return Math.Max(reading.Skew + jump, state.HighWater - utc);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var meter = meters.Create(Telemetry.MeterName);
        _ = meter.CreateObservableGauge("terrabase.clock.skew", () => SkewMilliseconds, "ms");
        _ = meter.CreateObservableGauge("terrabase.clock.local_writes_allowed", () => Allowed ? 1 : 0);
        _ = meter.CreateObservableGauge("terrabase.clock.reported", () => Volatile.Read(ref _reported));
        await using var reports = new ClockReports(options);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            var sampleAt = _elapsed.GetTimestamp();
            var sampleUtc = time.GetUtcNow().ToUnixTimeMilliseconds();
            var sampleAfter = _elapsed.GetTimestamp();
            var skew = double.PositiveInfinity;
            try
            {
                await state.RefreshAsync(stoppingToken).ConfigureAwait(false);
                var correction = await errors.CorrectionMillisecondsAsync(stoppingToken).ConfigureAwait(false);
                var before = DateTimeOffset.UtcNow;
                var application = time.GetUtcNow();
                var after = DateTimeOffset.UtcNow;
                skew = Math.Max(Math.Abs((application - before).TotalMilliseconds - correction),
                    Math.Abs((application - after).TotalMilliseconds - correction));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageBusyException)
            {
                skew = double.PositiveInfinity;
            }

            Volatile.Write(ref _reading, new Reading(skew, sampleAt, sampleAfter, sampleUtc));
            var value = SkewMilliseconds;
            var stored = await reports.PublishAsync(new ClockReport(options.MachineId, options.Region, double.IsFinite(value) ? value : null, value <= LimitMilliseconds, time.GetUtcNow()), stoppingToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _reported, stored ? 1 : 0);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private sealed record Reading(double Skew, long At, long After, long Utc);
}
