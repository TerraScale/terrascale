namespace TerraScale.DotNet.Tests;

/// <summary>
/// The per-region RTT tracker (ADR 0050 §2): a 60 s halflife exponentially
/// weighted moving average over bounded samples, serialized as the
/// discovery request's `X-TerraScale-Region-Rtt` header.
/// </summary>
public sealed class TerraScaleRegionRttTests
{
    private static long Minutes(double minutes) => (long)(minutes * 60_000);

    [Test]
    public async Task FirstSampleBecomesTheRegionsAverage()
    {
        var tracker = new TerraScaleRegionRttTracker { NowMs = () => 0 };

        tracker.Observe("FRA", 120);

        await Assert.That(tracker.MeasuredRttHeader()).IsEqualTo("FRA=120");
    }

    [Test]
    public async Task SamplesDecayWithASixtySecondHalflife()
    {
        var now = 0L;
        var tracker = new TerraScaleRegionRttTracker { NowMs = () => now };

        tracker.Observe("FRA", 100);
        now = Minutes(1);
        tracker.Observe("FRA", 200);

        // One halflife elapsed: the old average keeps half the weight.
        await Assert.That(tracker.MeasuredRttHeader()).IsEqualTo("FRA=150");

        now = Minutes(2);
        tracker.Observe("FRA", 100);

        // A quarter of the weight remains from the 150 average.
        await Assert.That(tracker.MeasuredRttHeader()).IsEqualTo("FRA=125");
    }

    [Test]
    public async Task OutOfRangeSamplesAreIgnored()
    {
        var tracker = new TerraScaleRegionRttTracker { NowMs = () => 0 };

        tracker.Observe("FRA", -1);
        tracker.Observe("FRA", 60_001);
        tracker.Observe("FRA", double.NaN);
        tracker.Observe("NOWHERE", 100);

        await Assert.That(tracker.MeasuredRttHeader()).IsNull();
    }

    [Test]
    public async Task TimeMovingBackwardsReplacesTheSample()
    {
        var now = 5_000L;
        var tracker = new TerraScaleRegionRttTracker { NowMs = () => now };

        tracker.Observe("FRA", 100);
        now = 1_000;
        tracker.Observe("FRA", 300);

        await Assert.That(tracker.MeasuredRttHeader()).IsEqualTo("FRA=300");
    }

    [Test]
    public async Task HeaderRoundsToWholeMillisecondsAndSkipsSubMillisecondAverages()
    {
        var now = 0L;
        var tracker = new TerraScaleRegionRttTracker { NowMs = () => now };

        tracker.Observe("GRU", 0.4);
        tracker.Observe("FRA", 120.6);

        await Assert.That(tracker.MeasuredRttHeader()).IsEqualTo("FRA=121");
    }

    [Test]
    public async Task ConcurrentObservationsStayConsistent()
    {
        var tracker = new TerraScaleRegionRttTracker { NowMs = () => 0 };

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            tracker.Observe("FRA", 100);
            tracker.Observe("GRU", 200);
            tracker.MeasuredRttHeader();
        })));

        var pairs = tracker.MeasuredRttHeader()!.Split(',');
        await Assert.That(pairs).IsEquivalentTo(["FRA=100", "GRU=200"]);
    }
}
