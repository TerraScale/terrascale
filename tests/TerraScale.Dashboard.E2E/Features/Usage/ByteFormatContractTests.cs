using TerraScale.Dashboard.Common.Formatting;

namespace TerraScale.Dashboard.E2E.Features.Usage
{
    // Storage reads in the decimal units the published catalog prices and TerraBase enforces.
    public sealed class ByteFormatContractTests
    {
        [Test]
        [Arguments(0L, "0 B")]
        [Arguments(999L, "999 B")]
        [Arguments(1_000L, "1.0 kB")]
        [Arguments(1_536L, "1.5 kB")]
        [Arguments(999_949L, "999.9 kB")]
        [Arguments(999_999L, "1.0 MB")]
        [Arguments(100_000_000L, "100.0 MB")]
        [Arguments(999_999_999L, "1.0 GB")]
        [Arguments(1_000_000_000L, "1.0 GB")]
        [Arguments(5_368_709_120L, "5.4 GB")]
        [Arguments(2_000_000_000_000L, "2.0 TB")]
        [Arguments(3_000_000_000_000_000L, "3.0 PB")]
        [Arguments(long.MaxValue, "9223.4 PB")]
        public async Task BytesUseDecimalUnitsAndPromoteOnTheRoundedValue(long bytes, string expected)
        {
            await Assert.That(MetricFormat.Bytes(bytes)).IsEqualTo(expected);
        }

        [Test]
        public async Task PlanStorageAllowancesReadAsTheirPublishedGigabytes()
        {
            await Assert.That(MetricFormat.Bytes(PlanCatalog.Limits("free").StorageBytes)).IsEqualTo("100.0 MB");
            await Assert.That(MetricFormat.Bytes(PlanCatalog.Limits("starter").StorageBytes)).IsEqualTo("1.0 GB");
            await Assert.That(MetricFormat.Bytes(PlanCatalog.Limits("pro").StorageBytes)).IsEqualTo("10.0 GB");
        }
    }
}
