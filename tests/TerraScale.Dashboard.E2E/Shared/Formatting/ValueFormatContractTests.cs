using System.Globalization;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.Shared.Formatting;

namespace TerraScale.Dashboard.E2E.Shared.Formatting;

public sealed class ValueFormatContractTests
{
    [Test]
    [Arguments(0L, "document", null, "0 documents")]
    [Arguments(1L, "database", null, "1 database")]
    [Arguments(2L, "organization", null, "2 organizations")]
    [Arguments(1L, "row", null, "1 row")]
    [Arguments(1000L, "read", null, "1,000 reads")]
    [Arguments(2L, "match", "matches", "2 matches")]
    [Arguments(1L, "person", "people", "1 person")]
    public async Task CountsUseSingularPluralAndInvariantGrouping(long count, string noun, string? plural, string expected) =>
        await Assert.That(ValueFormat.Plural(count, noun, plural)).IsEqualTo(expected);

    [Test]
    public async Task DatesConvertOffsetsToUtcAndIgnoreServerCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            await Assert.That(ValueFormat.UtcDate(new(2026, 10, 26, 23, 45, 0, TimeSpan.FromHours(-3))))
                .IsEqualTo("Oct 27, 2026 02:45 UTC");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Test]
    [Arguments(0L, 10000L, "0%")]
    [Arguments(1L, 10000L, "<1%")]
    [Arguments(99L, 10000L, "<1%")]
    [Arguments(100L, 10000L, "1%")]
    [Arguments(125L, 10000L, "1%")]
    [Arguments(150L, 10000L, "2%")]
    [Arguments(15000L, 10000L, "150%")]
    [Arguments(0L, 0L, "0%")]
    public async Task ShareAndAllowanceLabelsAgreeWithoutInflatingTinyShares(long part, long total, string expected)
    {
        var percent = ValueFormat.Percent(part, total)!.Value;
        await Assert.That(ValueFormat.PercentLabel(percent)).IsEqualTo(expected);
        await Assert.That(new UsageAllowance("Reads", "10,000", "1", percent, "Pro").Label)
            .IsEqualTo("Reads: " + expected + " of the Pro plan's allowance");
        if (part > 0 && total > 0)
            await Assert.That(percent).IsEqualTo(part / (double)total * 100);
        await Assert.That(ValueFormat.Percent(null, total)).IsNull();
        await Assert.That(ValueFormat.Percent(part, null)).IsNull();
    }

    [Test]
    [Arguments(null, "Not reported")]
    [Arguments(0d, "0.00 ms")]
    [Arguments(1.75d, "1.75 ms")]
    [Arguments(18.4d, "18.4 ms")]
    [Arguments(256d, "256 ms")]
    [Arguments(999.9d, "1,000 ms")]
    [Arguments(1234.5d, "1,235 ms")]
    public async Task LatencyUsesOnePrecisionAndInvariantGrouping(double? value, string expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            await Assert.That(MetricFormat.Latency(value)).IsEqualTo(expected);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
