using System.Globalization;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Common.Formatting;

namespace TerraScale.Dashboard.E2E.Common.Formatting
{
    public sealed class MoneyFormatContractTests
    {
        [Test]
        [Arguments(0L, "usd", "$0.00")]
        [Arguments(2000L, "USD", "$20.00")]
        [Arguments(-500L, "usd", "-$5.00")]
        [Arguments(long.MinValue, "usd", "-$92233720368547758.08")]
        [Arguments(1234L, "eur", "EUR 12.34")]
        [Arguments(-1234L, "eur", "-EUR 12.34")]
        [Arguments(1500L, "jpy", "JPY 1500")]
        [Arguments(-1500L, "jpy", "-JPY 1500")]
        [Arguments(1500L, "isk", "ISK 15.00")]
        [Arguments(1500L, "ugx", "UGX 15.00")]
        [Arguments(500L, "", "5.00 (currency unreported)")]
        public async Task CurrencyAndSignStayConsistentAcrossServerCultures(long cents, string currency, string expected)
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
                await Assert.That(MoneyFormat.Money(cents, currency)).IsEqualTo(expected);
            }
            finally { CultureInfo.CurrentCulture = original; }
        }

        [Test]
        public async Task OverageRoundsHalfCentsAwayFromZeroBeforeFormatting()
        {
            await Assert.That(MoneyFormat.Money(OperationUnitOverage.Cents(999))).IsEqualTo("$0.00");
            await Assert.That(MoneyFormat.Money(OperationUnitOverage.Cents(1000))).IsEqualTo("$0.01");
            await Assert.That(MoneyFormat.Money(OperationUnitOverage.Cents(-1000))).IsEqualTo("-$0.01");
            await Assert.That(MoneyFormat.Money(OperationUnitOverage.Cents(123_456))).IsEqualTo("$0.62");
        }
    }
}
