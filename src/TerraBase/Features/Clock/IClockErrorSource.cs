namespace TerraBase.Features.Clock;

internal interface IClockErrorSource
{
    ValueTask<double> CorrectionMillisecondsAsync(CancellationToken cancellationToken);
}
