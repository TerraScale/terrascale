using TerraBase.Features.Clock;

namespace TerraBase.Testing;

internal sealed class TestClockErrorSource(double correctionMilliseconds = 0) : IClockErrorSource
{
    public ValueTask<double> CorrectionMillisecondsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(correctionMilliseconds);
}
