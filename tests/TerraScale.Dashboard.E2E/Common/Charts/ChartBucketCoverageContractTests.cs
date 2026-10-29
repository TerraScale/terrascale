using TerraScale.Dashboard.Features.Analytics;

namespace TerraScale.Dashboard.E2E.Common.Charts
{
    public sealed class ChartBucketCoverageContractTests
    {
        [Test]
        [Arguments("1h", 60, 60)]
        [Arguments("24h", 24, 3600)]
        [Arguments("7d", 28, 21600)]
        [Arguments("7d", 168, 3600)]
        [Arguments("30d", 30, 86400)]
        [Arguments("30d", 720, 3600)]
        public async Task EveryBucketIncludingANewestSpikeHasItsOwnInspectableColumn(string range, int count, int seconds)
        {
            var first = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            var starts = Enumerable.Range(0, count).Select(index => first.AddSeconds((double)index * seconds)).ToArray();
            var values = Enumerable.Range(0, count).Select(index => index == count - 1 ? 987_654m : index + 1).ToArray();
            var plot = AnalyticsLinePlot.For("Reads, " + range, range, starts, [new("Reads", values)]);

            await Assert.That(plot.Columns.Count).IsEqualTo(count).Because("Each plotted bucket is reachable by hover and keyboard.");
            await Assert.That(plot.Columns[0].Rows[0].Value).IsEqualTo("1");
            await Assert.That(plot.Columns[^1].Header).IsEqualTo(starts[^1].UtcDateTime.ToString("MMM d HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture));
            await Assert.That(plot.Columns[^1].Rows[0].Value).IsEqualTo("987,654").Because("The newest spike is exposed, not skipped.");
            await Assert.That(plot.Columns.Select(column => column.Rows[0].Value)).IsEquivalentTo(values.Select(value => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
