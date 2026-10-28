using System.Runtime.Versioning;
using ApiBench;
using TUnit.Core;

namespace BenchClient.Tests;

[SupportedOSPlatform("linux")]
public sealed class ParityReviewTests
{
    [Test]
    [Arguments(null, "operation")]
    [Arguments("document", "document")]
    public async Task SuccessfulHeadersCannotOverrideAnOperationFailure(string? failure, string expected)
    {
        var window = await ParityWindow.RunAsync(_ => Task.FromException(new InvalidOperationException("synthetic worker failure")),
            new ParityWindowPlan(1, TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), TimeProvider.System, default);
        var successfulHeaders = new ParityAttemptSnapshot(window.Starts, window.Starts, window.Starts, 0, 0, 0, 0, 0);
        await Assert.That(window.Rejection).IsEqualTo("operation");
        await Assert.That(ParityCell.Rejection(window, failure, default, successfulHeaders)).IsEqualTo(expected);
    }
}
