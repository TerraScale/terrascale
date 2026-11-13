namespace TerraBase.Features.Clock;

internal sealed record ClockTracking(DateTimeOffset ReferenceTime, double CorrectionMilliseconds, double ErrorMilliseconds);

internal interface IClockErrorSource
{
    ValueTask<ClockTracking?> ReadAsync(CancellationToken cancellationToken);
}
