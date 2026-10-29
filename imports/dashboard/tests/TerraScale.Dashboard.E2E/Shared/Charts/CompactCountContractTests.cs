using System.Globalization;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Shared.Charts;

namespace TerraScale.Dashboard.E2E.Shared.Charts;

public sealed class CompactCountContractTests
{
    [Test]
    [Arguments(0d, "0")]
    [Arguments(999d, "999")]
    [Arguments(1000d, "1.0k")]
    [Arguments(1200d, "1.2k")]
    [Arguments(999949d, "999.9k")]
    [Arguments(999950d, "1.0M")]
    [Arguments(1e6, "1.0M")]
    [Arguments(999950000d, "1.0B")]
    [Arguments(1.5e9, "1.5B")]
    [Arguments(999950000000d, "1.0T")]
    [Arguments(2e12, "2.0T")]
    [Arguments(-999d, "-999")]
    [Arguments(-999950d, "-1.0M")]
    [Arguments(-1.5e9, "-1.5B")]
    public async Task CountsUseInvariantSuffixesAndPromoteRoundedBoundaries(double value, string expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            await Assert.That(ChartGeometry.Compact(value)).IsEqualTo(expected);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Test]
    public async Task AnalyticsAxesShareCompactFormattingWhileTooltipsKeepExactCounts()
    {
        var plot = AnalyticsLinePlot.For("Operations", "1h", [DateTimeOffset.UtcNow], [new("Reads", [5_612_334_001])]);
        await Assert.That(plot.Grid[0].Label).IsEqualTo("8.0B");
        await Assert.That(plot.Columns[0].Rows[0].Value).IsEqualTo("5,612,334,001");
    }
}
